using System.Windows.Media.Imaging;
using FotoArchiv.App.Models;
using ImageMagick;

namespace FotoArchiv.App.Services;

public sealed class ThumbnailService
{
    public Task<BitmapSource?> LoadAsync(MediaItem? item, CancellationToken cancellationToken)
    {
        if (item?.Kind != MediaKind.Image || !File.Exists(item.FilePath))
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        return Task.Run<BitmapSource?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var image = new MagickImage(item.FilePath);
                image.AutoOrient();
                image.Thumbnail(720, 520);
                image.Strip();
                var bytes = image.ToByteArray(MagickFormat.Jpeg);
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
    }
}
