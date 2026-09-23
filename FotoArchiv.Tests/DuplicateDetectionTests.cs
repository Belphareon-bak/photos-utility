using FotoArchiv.App.Models;
using FotoArchiv.App.Services;
using ImageMagick;

namespace FotoArchiv.Tests;

public sealed class DuplicateDetectionTests
{
    [Fact]
    public async Task Detect_FindsBitIdenticalFilesWithAbsoluteConfidence()
    {
        using var directory = new TestDirectory();
        var firstPath = directory.File("one.mp4");
        var secondPath = directory.File("two.mp4");
        var bytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(firstPath, bytes);
        await File.WriteAllBytesAsync(secondPath, bytes);
        var first = TestMedia.Create(firstPath, directory.Path, MediaKind.Video, fileSize: bytes.Length);
        var second = TestMedia.Create(secondPath, directory.Path, MediaKind.Video, fileSize: bytes.Length);

        var groups = await new DuplicateDetectionService().DetectAsync([first, second], null, CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(DuplicateConfidence.Exact, group.Confidence);
        Assert.Equal(2, group.Items.Count);
    }

    [Fact]
    public async Task Detect_FindsSameRenderedImageWithDifferentBytesAsHighConfidence()
    {
        using var directory = new TestDirectory();
        var firstPath = directory.File("one.jpg");
        var secondPath = directory.File("two.jpg");
        using (var image = new MagickImage(MagickColors.DarkGreen, 320, 240))
        {
            image.Format = MagickFormat.Jpeg;
            image.Write(firstPath);
            image.Write(secondPath);
        }
        await File.AppendAllBytesAsync(secondPath, [1, 2, 3, 4]);
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        var first = TestMedia.Create(firstPath, directory.Path, fileSize: firstInfo.Length, width: 320, height: 240);
        var second = TestMedia.Create(secondPath, directory.Path, fileSize: secondInfo.Length, width: 320, height: 240);

        var groups = await new DuplicateDetectionService().DetectAsync([first, second], null, CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(DuplicateConfidence.High, group.Confidence);
    }

    [Fact]
    public async Task Detect_DoesNotGiveHighConfidenceToDifferentFlatColors()
    {
        using var directory = new TestDirectory();
        var firstPath = directory.File("green.jpg");
        var secondPath = directory.File("red.jpg");
        using (var green = new MagickImage(MagickColors.DarkGreen, 320, 240)) green.Write(firstPath);
        using (var red = new MagickImage(MagickColors.DarkRed, 320, 240)) red.Write(secondPath);
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        var first = TestMedia.Create(firstPath, directory.Path, fileSize: firstInfo.Length, width: 320, height: 240);
        var second = TestMedia.Create(secondPath, directory.Path, fileSize: secondInfo.Length, width: 320, height: 240);

        var groups = await new DuplicateDetectionService().DetectAsync([first, second], null, CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(DuplicateConfidence.Low, group.Confidence);
    }
}
