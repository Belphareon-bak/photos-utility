using FotoArchiv.App.Models;

namespace FotoArchiv.Tests;

internal static class TestMedia
{
    public static MediaItem Create(
        string path,
        string root,
        MediaKind kind = MediaKind.Image,
        DateTimeOffset? capturedAt = null,
        string? location = "Okoř",
        int width = 4032,
        int height = 3024,
        long fileSize = 1_000_000) => new()
        {
            FilePath = path,
            SourceRoot = root,
            Kind = kind,
            FileSize = fileSize,
            FileCreatedAt = DateTimeOffset.Now,
            FileModifiedAt = DateTimeOffset.Now,
            CapturedAt = capturedAt ?? new DateTimeOffset(2025, 9, 12, 14, 30, 0, TimeSpan.Zero),
            LocationName = location,
            Width = width,
            Height = height,
            BundleKey = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileNameWithoutExtension(path)).ToUpperInvariant()
        };
}
