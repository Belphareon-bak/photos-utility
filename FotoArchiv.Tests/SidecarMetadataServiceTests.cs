using System.Text;
using FotoArchiv.App.Models;
using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class SidecarMetadataServiceTests
{
    [Fact]
    public async Task Apply_UsesGoogleTakeoutDateAndGpsWhenPhotoMetadataIsMissing()
    {
        using var directory = new TestDirectory();
        var photoPath = directory.File("IMG_0042.jpg");
        var jsonPath = directory.File("IMG_0042.jpg.json");
        var photo = TestMedia.Create(photoPath, directory.Path, capturedAt: DateTimeOffset.Now, location: null);
        photo.CaptureDateSource = "Čas souboru";
        var sidecar = TestMedia.Create(jsonPath, directory.Path, MediaKind.Sidecar, location: null);
        sidecar.BundleKey = photo.BundleKey;
        const string json = """
            {
              "title": "IMG_0042.jpg",
              "photoTakenTime": { "timestamp": "1757687415" },
              "geoDataExif": { "latitude": 50.16191, "longitude": 14.25862 }
            }
            """;
        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);

        await new SidecarMetadataService().ApplyAsync([photo, sidecar], null, CancellationToken.None);

        Assert.Equal("Google Takeout JSON", photo.CaptureDateSource);
        Assert.Equal(50.16191, photo.Latitude);
        Assert.Equal(14.25862, photo.Longitude);
    }
}
