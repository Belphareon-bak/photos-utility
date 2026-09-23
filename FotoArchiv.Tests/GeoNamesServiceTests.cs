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
}
