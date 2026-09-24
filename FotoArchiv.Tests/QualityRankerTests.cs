using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class QualityRankerTests
{
    [Fact]
    public void SelectBest_PrefersFullResolutionJpgOverSmallPng()
    {
        // PNG ma vyssi poradi formatu nez JPG, ale zmensene PNG nesmi porazit originalni
        // JPG s mnohonasobne vetsim rozlisenim.
        var original = TestMedia.Create("/a/IMG_0001.jpg", "/a", width: 4032, height: 3024);
        var screenshot = TestMedia.Create("/a/IMG_0001.png", "/a", width: 1080, height: 810);

        Assert.Same(original, QualityRanker.SelectBest([screenshot, original]));
    }

    [Fact]
    public void SelectBest_RawStillWinsOverLargerJpg()
    {
        var raw = TestMedia.Create("/a/IMG_0001.dng", "/a", width: 4000, height: 3000);
        var jpg = TestMedia.Create("/a/IMG_0001.jpg", "/a", width: 4032, height: 3024);

        Assert.Same(raw, QualityRanker.SelectBest([jpg, raw]));
    }

    [Fact]
    public void SelectBest_PrefersRawOriginalOverSmallerDerivative()
    {
        using var directory = new TestDirectory();
        var raw = TestMedia.Create(directory.File("photo.dng"), directory.Path, width: 4000, height: 3000, fileSize: 20_000_000);
        var jpeg = TestMedia.Create(directory.File("photo.jpg"), directory.Path, width: 5000, height: 3750, fileSize: 4_000_000);

        var selected = QualityRanker.SelectBest([jpeg, raw]);

        Assert.Same(raw, selected);
        Assert.True(raw.IsRecommendedKeep);
        Assert.False(jpeg.IsRecommendedKeep);
    }
}
