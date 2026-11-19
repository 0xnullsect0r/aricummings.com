using System.Security.Cryptography;
using Ari.Platform;
using Ari.Platform.Database;
using Dapper;

namespace Ari.Features.Media;

/// <summary>
/// The build-media CLI: processes seed images through the same pipeline as uploads, so both
/// produce identical files. Usage: set ARI_BUILD_MEDIA_DIR to the folder of images, then
/// run the app as "Ari build-media". Idempotent: an image already in the library keeps its
/// row and files.
/// </summary>
public sealed class BuildMediaCli : ICliMode
{
    public string Name => "build-media";

    public Task<int> Run(Env env, string[] args)
    {
        // "ari build-media --source <dir>" or the ARI_BUILD_MEDIA_DIR env var.
        string? source = null;
        for (var i = 1; i + 1 < args.Length; i++)
            if (args[i] == "--source") source = args[i + 1];
        source ??= args.Skip(1).FirstOrDefault(a => a != "--source");
        source ??= Environment.GetEnvironmentVariable("ARI_BUILD_MEDIA_DIR");
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            Console.Error.WriteLine("Usage: Ari build-media --source <folder-of-images> (or set ARI_BUILD_MEDIA_DIR).");
            return Task.FromResult(2);
        }

        var db = new Db(env.DataDir);
        var processor = new MediaProcessor(env);
        var store = new MediaStore(db, env);
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(source).Order())
        {
            var bytes = File.ReadAllBytes(path);
            if (MediaImage.Detect(bytes) is MediaFormat.Unknown or MediaFormat.Heic)
            {
                Console.WriteLine($"skipped {Path.GetFileName(path)} (not a processable image)");
                continue;
            }

            var image = processor.ProcessAsync(bytes, Path.GetFileName(path), CancellationToken.None)
                .GetAwaiter().GetResult();
            using var connection = db.App();
            Db.Write(connection, transaction =>
            {
                store.Insert(connection, transaction, image, alt: null, actor: null).GetAwaiter().GetResult();
                return 0;
            });
            Console.WriteLine($"{image.Id}  {Path.GetFileName(path)}  {image.Width}x{image.Height}");
            count++;
        }
        Console.WriteLine($"{count} image(s) processed into {env.MediaDir}");
        return Task.FromResult(0);
    }
}
