using FotoArchiv.App.Models;

namespace FotoArchiv.App.Services;

public sealed class MediaScanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".png", ".webp", ".tif", ".tiff",
        ".dng", ".arw", ".cr2", ".cr3", ".nef", ".orf", ".raf", ".rw2"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".avi"
    };

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aae", ".xmp", ".json"
    };

    public IReadOnlyList<(string Path, string Root, MediaKind Kind)> EnumerateFiles(
        IEnumerable<SourceFolder> sources,
        Action<string>? warning,
        CancellationToken cancellationToken)
    {
        var files = new List<(string Path, string Root, MediaKind Kind)>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources.Where(source => source.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source.Path))
            {
                warning?.Invoke($"Zdroj není dostupný: {source.Path}");
                continue;
            }

            var root = Path.GetFullPath(source.Path);
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        if (!IsIgnoredDirectory(child) && !IsReparsePoint(child))
                        {
                            pending.Push(child);
                        }
                    }

                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        if (TryGetKind(file, out var kind))
                        {
                            var fullPath = Path.GetFullPath(file);
                            if (seenFiles.Add(fullPath))
                            {
                                files.Add((fullPath, root, kind));
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    warning?.Invoke($"Nelze načíst {directory}: {exception.Message}");
                }
            }
        }

        return files;
    }

    private static bool TryGetKind(string path, out MediaKind kind)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension))
        {
            kind = MediaKind.Image;
            return true;
        }

        if (VideoExtensions.Contains(extension))
        {
            kind = MediaKind.Video;
            return true;
        }

        if (SidecarExtensions.Contains(extension))
        {
            kind = MediaKind.Sidecar;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsIgnoredDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.') ||
               name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("_DuplicatesReview", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }
}
