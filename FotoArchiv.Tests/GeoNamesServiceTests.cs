using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class GeoNamesServiceTests
{
    [Fact]
    public async Task Resolve_UsesDetailedCountryDatasetForOkor()
    {
        using var directory = new TestDirectory();
        var item = TestMedia.Create(directory.File("photo.jpg"), directory.Path, location: null);
        item.Latitude = 50.16191;
        item.Longitude = 14.25862;
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        var service = new GeoNamesService(dataDirectory);

        await service.ResolveAsync([item], null, CancellationToken.None);

        Assert.Equal("Okoř", item.LocationName);
        Assert.Equal("CZ", item.CountryCode);
        Assert.False(string.IsNullOrWhiteSpace(item.RegionName));
    }

    [Fact]
    public async Task Resolve_IgnoresZeroCoordinates()
    {
        // Telefony bez signalu i Google Takeout zapisuji "bez polohy" jako 0, 0.
        // Nejblizsi obec k tomu bodu lezi v Ghane - fotka tam nesmi skoncit.
        using var directory = new TestDirectory();
        var item = TestMedia.Create(directory.File("photo.jpg"), directory.Path, location: null);
        item.Latitude = 0;
        item.Longitude = 0;

        await new GeoNamesService(Path.Combine(AppContext.BaseDirectory, "Data"))
            .ResolveAsync([item], null, CancellationToken.None);

        Assert.Null(item.LocationName);
    }

    [Fact]
    public async Task Resolve_DoesNotNameFarAwayPlace()
    {
        // Uprostred Atlantiku je nejblizsi obec stovky kilometru daleko.
        using var directory = new TestDirectory();
        var item = TestMedia.Create(directory.File("photo.jpg"), directory.Path, location: null);
        item.Latitude = 40.0;
        item.Longitude = -40.0;

        await new GeoNamesService(Path.Combine(AppContext.BaseDirectory, "Data"))
            .ResolveAsync([item], null, CancellationToken.None);

        Assert.Null(item.LocationName);
    }
}
