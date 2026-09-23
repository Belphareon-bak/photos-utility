using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class QualityRankerTests
{
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
