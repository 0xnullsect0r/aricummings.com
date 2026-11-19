using System.Security.Cryptography;
using Ari.Platform;
using Ari.Platform.Api;
using NetVips;

namespace Ari.Features.Media;

public sealed record ProcessedImage(string Id, int Width, int Height, long Bytes, string Sha256, string OriginalName);

/// <summary>
/// The one image pipeline shared by uploads and the build-media CLI, so both produce
/// identical files. Identity is the first 16 hex characters of the SHA-256 of the original;
/// content-addressed files never change, so they are served immutable.
///
/// Renditions: AVIF q55 effort 3 (no chroma subsampling) and WebP q78 at widths 400, 800,
/// 1200, 1600, 2400 — never larger than the source; a 64px blur WebP q50; and a 1200px
/// og.jpg q80 flattened on white. EXIF orientation is applied, everything is exported to
/// sRGB, and all metadata is stripped. The whole file is decoded once with failOn: Warning
/// before any rendition, so a cut-off original is refused instead of passing with a grey
/// bottom half.
/// </summary>
public sealed class MediaProcessor(Env env)
{
    private static readonly SemaphoreSlim EncodeGate = new(1, 1);
    private static readonly object InitLock = new();
    private static bool _configured;

    private static void ConfigureOnce()
    {
        lock (InitLock)
        {
            if (_configured) return;
            // The operation cache would hold on to decoded photos; this server has better
            // uses for the memory.
            NetVips.Cache.Max = 0;
            NetVips.Cache.MaxMem = 0;
            NetVips.Cache.MaxFiles = 0;
            _configured = true;
        }
    }

    /// <summary>Processes one upload on a thread pool thread, one encode at a time.</summary>
    public async Task<ProcessedImage> ProcessAsync(byte[] original, string originalName, CancellationToken cancellation)
    {
        MediaImage.AssertAcceptable(original);
        if (original.LongLength > MediaImage.MaxUploadBytes)
            throw new ApiException(413, "Images are limited to 40 MB.", "too_large");

        return await Task.Run(async () =>
        {
            await EncodeGate.WaitAsync(cancellation);
            try
            {
                ConfigureOnce();
                return Process(original, originalName);
            }
            finally
            {
                EncodeGate.Release();
            }
        }, cancellation);
    }

    private ProcessedImage Process(byte[] original, string originalName)
    {
        var sha = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        var id = sha[..16];

        // Header-only open for the megapixel refusal, before any full decode.
        using (var header = Image.NewFromBuffer(original, "", Enums.Access.Sequential, Enums.FailOn.None, null))
        {
            if ((long)header.Width * header.Height > MediaImage.MaxMegapixels)
                throw new ApiException(400, "Images are limited to 100 megapixels.", "too_many_megapixels");
        }

        // RequireWholeFile: a full decode with failOn: Warning, not shrink-on-load, so a
        // truncated original is refused. Autorot gives the true oriented dimensions.
        int width, height;
        using (var whole = Image.NewFromBuffer(original, "", Enums.Access.Random, Enums.FailOn.Warning, null))
        {
            using var oriented = whole.Autorot();
            width = oriented.Width;
            height = oriented.Height;
            _ = oriented.Avg(); // touch every pixel: this is the decode that proves the file
        }

        var directory = Path.Combine(env.MediaDir, id);
        if (Directory.Exists(directory) && File.Exists(Path.Combine(directory, "og.jpg")))
            return new ProcessedImage(id, width, height, original.LongLength, sha, originalName);

        // Write into a temp folder, then move into place: live URLs never see a
        // half-written set.
        var staging = Path.Combine(env.MediaDir, ".tmp", $"{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var targetWidth in MediaRenditions.WidthsFor(width))
            {
                WriteRendition(staging, original, targetWidth, $"{targetWidth}");
            }

            // 64px blur placeholder.
            using (var thumb = Thumbnail(original, 64, enlarge: true))
            using (var blurred = thumb.Gaussblur(8))
            {
                blurred.WebpsaveBuffer(q: 50, keep: Enums.ForeignKeep.None)
                    .WriteFile(Path.Combine(staging, "blur.webp"));
            }

            // 1200px og.jpg, flattened on white so transparency never turns black. Flatten
            // only when there is an alpha channel to lose: a greyscale source has none.
            using (var thumb = Thumbnail(original, Math.Min(1200, width), enlarge: false))
            {
                using var srgb = thumb.Colourspace(Enums.Interpretation.Srgb);
                if (srgb.Bands is 2 or 4)
                {
                    using var flat = srgb.Flatten(background: new double[] { 255, 255, 255 });
                    flat.JpegsaveBuffer(q: 80, keep: Enums.ForeignKeep.None)
                        .WriteFile(Path.Combine(staging, "og.jpg"));
                }
                else
                {
                    srgb.JpegsaveBuffer(q: 80, keep: Enums.ForeignKeep.None)
                        .WriteFile(Path.Combine(staging, "og.jpg"));
                }
            }

            if (Directory.Exists(directory))
            {
                // Same content address twice: replace the files as a set.
                var retired = Path.Combine(env.MediaDir, ".tmp", $"{id}-old-{Guid.NewGuid():N}");
                Directory.Move(directory, retired);
                Directory.Move(staging, directory);
                Directory.Delete(retired, recursive: true);
            }
            else
            {
                Directory.Move(staging, directory);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        return new ProcessedImage(id, width, height, original.LongLength, sha, originalName);
    }

    private static Image Thumbnail(byte[] original, int width, bool enlarge)
    {
        // Decode from the buffer for each size, so large JPEGs shrink on load.
        using var image = Image.NewFromBuffer(original, "", Enums.Access.Sequential, Enums.FailOn.Warning, null);
        return image.ThumbnailImage(
            width,
            height: null,
            size: enlarge ? Enums.Size.Both : Enums.Size.Down,
            noRotate: false);
    }

    private static void WriteRendition(string staging, byte[] original, int sourceWidth, string name)
    {
        using (var thumb = Thumbnail(original, sourceWidth, enlarge: false))
        {
            using var srgb = thumb.Colourspace(Enums.Interpretation.Srgb);
            srgb.HeifsaveBuffer(
                    q: 55,
                    effort: 3,
                    compression: Enums.ForeignHeifCompression.Av1,
                    subsampleMode: Enums.ForeignSubsample.Off,
                    keep: Enums.ForeignKeep.None)
                .WriteFile(Path.Combine(staging, $"{name}.avif"));
        }
        using (var thumb = Thumbnail(original, sourceWidth, enlarge: false))
        {
            using var srgb = thumb.Colourspace(Enums.Interpretation.Srgb);
            srgb.WebpsaveBuffer(q: 78, keep: Enums.ForeignKeep.None)
                .WriteFile(Path.Combine(staging, $"{name}.webp"));
        }
    }
}

internal static class ByteArrayFileExtensions
{
    /// <summary>Writes a produced rendition; the staging folder is private until it moves.</summary>
    public static void WriteFile(this byte[] bytes, string path) => File.WriteAllBytes(path, bytes);
}
