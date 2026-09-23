using System.Security.Cryptography;
using FotoArchiv.App.Models;
using ImageMagick;

namespace FotoArchiv.App.Services;

public sealed class HashService
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public static Task<ulong?> ComputePerceptualHashAsync(MediaItem item, CancellationToken cancellationToken)
    {
        if (item.Kind != MediaKind.Image)
        {
            return Task.FromResult<ulong?>(null);
        }

        return Task.Run<ulong?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var image = new MagickImage(item.FilePath);
                image.AutoOrient();

                using (var sample = image.Clone())
                {
                    sample.Resize(new MagickGeometry(64, 64) { IgnoreAspectRatio = true });
                    sample.ColorSpace = ColorSpace.sRGB;
                    sample.Depth = 8;
                    item.NormalizedPixelHash = Convert.ToHexString(
                        SHA256.HashData(sample.ToByteArray(MagickFormat.Rgb)));
                }

                image.Resize(new MagickGeometry(9, 8) { IgnoreAspectRatio = true });
                image.ColorSpace = ColorSpace.Gray;
                image.Depth = 8;
                var pixels = image.ToByteArray(MagickFormat.Gray);
                if (pixels.Length < 72)
                {
                    return null;
                }

                ulong hash = 0;
                var bit = 0;
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        if (pixels[(y * 9) + x] > pixels[(y * 9) + x + 1])
                        {
                            hash |= 1UL << bit;
                        }

                        bit++;
                    }
                }

                return hash;
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
    }

    public static int HammingDistance(ulong left, ulong right) =>
        System.Numerics.BitOperations.PopCount(left ^ right);
}
