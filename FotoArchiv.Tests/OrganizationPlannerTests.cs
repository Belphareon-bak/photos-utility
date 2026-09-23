using FotoArchiv.App.Models;
using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class OrganizationPlannerTests
{
    [Fact]
    public void Build_UsesLocationThenDateAndShortSequenceName()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        var photo = TestMedia.Create(source.File("IMG_1234.jpg"), source.Path);
        var planner = new OrganizationPlanner();

        var plan = planner.Build([photo], new AppSettings
        {
            DestinationRoot = destination.Path,
            SplitLocationByDate = true
        });

        Assert.Single(plan);
        Assert.Equal(System.IO.Path.Combine(destination.Path, "Okoř", "2025-09-12", "001.jpg"), plan[0].TargetPath);
    }

    [Fact]
    public void Build_MovesUnusableFileAsideAndKeepsNumberingIntact()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        var photo = TestMedia.Create(source.File("phone/IMG_1234.jpg"), source.Path);
        var notAPhoto = TestMedia.Create(source.File("phone/rozbity.jpg"), source.Path);
        notAPhoto.RejectionReason = "Soubor nelze nacist jako obrazek";

        var plan = new OrganizationPlanner().Build([photo, notAPhoto], new AppSettings
        {
            DestinationRoot = destination.Path,
            SplitLocationByDate = true
        });

        var archived = Assert.Single(plan, item => item.Media == photo);
        var setAside = Assert.Single(plan, item => item.Media == notAPhoto);

        // nepouzitelny soubor nesmi zabrat cislo v rade, jinak by v archivu vznikla dira
        Assert.Equal(Path.Combine(destination.Path, "Okoř", "2025-09-12", "001.jpg"), archived.TargetPath);
        Assert.Equal(Path.Combine(destination.Path, "_NeniFoto", "phone", "rozbity.jpg"), setAside.TargetPath);
        Assert.Equal("Soubor nelze nacist jako obrazek", setAside.Warning);
    }

    [Fact]
    public void Build_KeepsSidecarWithItsRejectedPrimary()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        var primary = TestMedia.Create(source.File("IMG_1234.jpg"), source.Path);
        primary.RejectionReason = "Soubor je prazdny";
        var sidecar = TestMedia.Create(source.File("IMG_1234.xmp"), source.Path, MediaKind.Sidecar);

        var plan = new OrganizationPlanner().Build([primary, sidecar], new AppSettings
        {
            DestinationRoot = destination.Path
        });

        // sidecar nesmi zustat osirely v archivu, kdyz jeho fotka odesla stranou
        Assert.Equal(2, plan.Count);
        Assert.All(plan, item => Assert.StartsWith(
            Path.Combine(destination.Path, "_NeniFoto"), item.TargetPath));
        Assert.DoesNotContain(plan, item => item.Action == PlannedAction.Skip);
    }

    [Fact]
    public void Build_CanMergeDatesInsideLocation()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        var first = TestMedia.Create(source.File("a.jpg"), source.Path,
            capturedAt: new DateTimeOffset(2025, 9, 12, 10, 0, 0, TimeSpan.Zero));
        var second = TestMedia.Create(source.File("b.jpg"), source.Path,
            capturedAt: new DateTimeOffset(2025, 9, 13, 10, 0, 0, TimeSpan.Zero));

        var plan = new OrganizationPlanner().Build([first, second], new AppSettings
        {
            DestinationRoot = destination.Path,
            SplitLocationByDate = false
        });

        Assert.Equal(System.IO.Path.Combine(destination.Path, "Okoř", "001.jpg"), plan[0].TargetPath);
        Assert.Equal(System.IO.Path.Combine(destination.Path, "Okoř", "002.jpg"), plan[1].TargetPath);
    }

    [Fact]
    public void Build_KeepsLivePhotoBundleOnSameBaseName()
    {
        using var source = new TestDirectory();
        using var destination = new TestDirectory();
        var imagePath = source.File("IMG_0077.heic");
        var videoPath = source.File("IMG_0077.mov");
        var image = TestMedia.Create(imagePath, source.Path);
        var video = TestMedia.Create(videoPath, source.Path, MediaKind.Video);
        video.BundleKey = image.BundleKey;

        var plan = new OrganizationPlanner().Build([image, video], new AppSettings { DestinationRoot = destination.Path });

        Assert.Contains(plan, item => item.TargetPath.EndsWith(System.IO.Path.Combine("2025-09-12", "001.heic")));
        Assert.Contains(plan, item => item.TargetPath.EndsWith(System.IO.Path.Combine("2025-09-12", "001.mov")));
    }
}
