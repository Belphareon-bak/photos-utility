using FotoArchiv.App.Models;
using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class MediaScannerTests
{
    [Fact]
    public void EnumerateFiles_DeduplicatesOverlappingRootsAndIgnoresQuarantine()
    {
        using var directory = new TestDirectory();
        var nested = Path.Combine(directory.Path, "phone");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "photo.jpg"), [1]);
        var quarantine = Path.Combine(directory.Path, "_DuplicatesReview");
        Directory.CreateDirectory(quarantine);
        File.WriteAllBytes(Path.Combine(quarantine, "duplicate.jpg"), [1]);

        var files = new MediaScanner().EnumerateFiles(
            [new SourceFolder(directory.Path), new SourceFolder(nested)], null, CancellationToken.None);

        var file = Assert.Single(files);
        Assert.EndsWith("photo.jpg", file.Path);
    }
}
