using FotoArchiv.App.Models;
using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class MetadataReaderServiceTests
{
    [Fact]
    public async Task Read_MarksFileThatOnlyPretendsToBeAnImage()
    {
        using var directory = new TestDirectory();
        var fake = directory.File("prejmenovany.jpg");
        await File.WriteAllTextAsync(fake, "Tohle je textovy soubor, ne fotografie.");

        var item = await new MetadataReaderService()
            .ReadAsync(fake, directory.Path, MediaKind.Image, CancellationToken.None);

        Assert.NotNull(item.RejectionReason);
    }

    [Fact]
    public async Task Read_TakesVideoDateFromQuickTimeHeaderAsUtc()
    {
        // Video natocene 2023-06-15 22:30 UTC. MetadataExtractor tu hodnotu pojmenovava
        // "Created"; driv ji FotoArchiv nehledal a vsechna videa spadla na cas souboru.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "video-utc-2023-06-15T2230Z.mp4");

        var item = await new MetadataReaderService()
            .ReadAsync(path, Path.GetDirectoryName(path)!, MediaKind.Video, CancellationToken.None);

        Assert.Equal("Created", item.CaptureDateSource);
        // okamzik musi sedet presne; v prazskem case jde o 2023-06-16 00:30.
        // Pozor: chybu "UTC brane jako mistni cas" tento test odhali jen tam, kde mistni
        // cas neni UTC - na stroji nastavenem na UTC davaji obe cesty stejny vysledek.
        Assert.Equal(new DateTime(2023, 6, 15, 22, 30, 0, DateTimeKind.Utc), item.CapturedAt!.Value.UtcDateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(item.CapturedAt.Value.UtcDateTime), item.CapturedAt.Value.Offset);
    }

    [Fact]
    public async Task Read_IgnoresQuickTimeZeroDate()
    {
        // Video bez nastaveneho data ma v hlavicce nulu formatu QuickTime = 1904-01-01.
        // Takove datum se nesmi pouzit, jinak video skonci ve slozce 1904.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "video-bez-data.mp4");

        var item = await new MetadataReaderService()
            .ReadAsync(path, Path.GetDirectoryName(path)!, MediaKind.Video, CancellationToken.None);

        Assert.NotEqual("Created", item.CaptureDateSource);
        Assert.True(item.CapturedAt!.Value.Year > 1904);
    }

    [Fact]
    public async Task Read_SurvivesFileThatDisappearedAfterScan()
    {
        using var directory = new TestDirectory();
        var missing = Path.Combine(directory.Path, "zmizel.jpg");

        var item = await new MetadataReaderService()
            .ReadAsync(missing, directory.Path, MediaKind.Image, CancellationToken.None);

        Assert.Equal("Soubor nelze otevrit", item.RejectionReason);
    }

    [Fact]
    public async Task Read_MarksEmptyFile()
    {
        using var directory = new TestDirectory();
        var empty = directory.File("prazdny.jpg");
        await File.WriteAllBytesAsync(empty, []);

        var item = await new MetadataReaderService()
            .ReadAsync(empty, directory.Path, MediaKind.Image, CancellationToken.None);

        Assert.Equal("Soubor je prazdny", item.RejectionReason);
    }

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
