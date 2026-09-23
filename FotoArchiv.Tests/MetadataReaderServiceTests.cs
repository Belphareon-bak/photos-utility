using FotoArchiv.App.Models;
using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class MetadataReaderServiceTests
{
    [Fact]
    public async Task Read_UsesPixelFilenameWithMillisecondsWhenExifIsUnavailable()
    {
        using var directory = new TestDirectory();
        var path = directory.File("PXL_20250912_143015235.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        var item = await new MetadataReaderService().ReadAsync(path, directory.Path, MediaKind.Image, CancellationToken.None);

        Assert.NotNull(item.CapturedAt);
        Assert.Equal(new DateTime(2025, 9, 12, 14, 30, 15), item.CapturedAt.Value.DateTime);
        Assert.Equal("Název souboru", item.CaptureDateSource);
    }
}
