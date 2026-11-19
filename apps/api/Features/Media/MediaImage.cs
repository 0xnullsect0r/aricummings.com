using System.Text;
using Ari.Platform.Api;

namespace Ari.Features.Media;

public enum MediaFormat { Jpeg, Png, Webp, Avif, Tiff, Heic, Unknown }

/// <summary>
/// The format comes from the file's bytes, never the name or the type the browser claims,
/// so nothing else can slip in. HEIC gets its own message.
/// </summary>
public static class MediaImage
{
    private static readonly string[] HeicBrands =
    {
        "heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1", "heif",
    };

    public const string HeicMessage = "iPhone HEIC photos need converting to JPEG first.";
    public const long MaxMegapixels = 100_000_000;
    public const int MaxUploadBytes = 40 * 1024 * 1024;

    public static MediaFormat Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return MediaFormat.Jpeg;
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return MediaFormat.Png;
        if (bytes.Length >= 4 && bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) return MediaFormat.Tiff;
        if (bytes.Length >= 4 && bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A) return MediaFormat.Tiff;
        if (bytes.Length >= 12)
        {
            if (Encoding.ASCII.GetBytes("RIFF").AsSpan().SequenceEqual(bytes[..4])
                && Encoding.ASCII.GetBytes("WEBP").AsSpan().SequenceEqual(bytes.Slice(8, 4))) return MediaFormat.Webp;
            if (Encoding.ASCII.GetBytes("ftyp").AsSpan().SequenceEqual(bytes.Slice(4, 4)))
            {
                var brand = Encoding.ASCII.GetString(bytes.Slice(8, 4)).ToLowerInvariant();
                if (brand is "avif" or "avis") return MediaFormat.Avif;
                if (HeicBrands.Contains(brand)) return MediaFormat.Heic;
            }
        }
        return MediaFormat.Unknown;
    }

    /// <summary>Refuses everything the pipeline will not process, with the reason.</summary>
    public static void AssertAcceptable(ReadOnlySpan<byte> bytes)
    {
        var format = Detect(bytes);
        switch (format)
        {
            case MediaFormat.Heic:
                throw new ApiException(400, HeicMessage, "heic_unsupported");
            case MediaFormat.Unknown:
                throw new ApiException(400, "That file is not a JPEG, PNG, WebP, AVIF or TIFF image.", "bad_format");
        }
    }

    public static string ExtensionFor(MediaFormat format) => format switch
    {
        MediaFormat.Jpeg => "jpg",
        MediaFormat.Png => "png",
        MediaFormat.Webp => "webp",
        MediaFormat.Avif => "avif",
        MediaFormat.Tiff => "tiff",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

/// <summary>
/// Which renditions exist for a source of a given width: the configured widths, never
/// larger than the source. A source narrower than 400 gets one rendition at its natural
/// width, so a page can always render something.
/// </summary>
public static class MediaRenditions
{
    public static readonly int[] Widths = { 400, 800, 1200, 1600, 2400 };

    public static int[] WidthsFor(int sourceWidth)
    {
        var fitting = Widths.Where(w => w <= sourceWidth).ToArray();
        return fitting.Length > 0 ? fitting : new[] { sourceWidth };
    }
}
