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

    /// <param name="ignoredFolderNames">
    /// Jmena sluzebnich slozek, ktere se maji preskocit navic k vychozim. Patri sem
    /// nazvy z nastaveni (karantena, odlozene soubory) - kdyz se nepredaji, sken je
    /// pri dalsim behu nacte znovu jako zdrojova data.
    /// </param>
    public IReadOnlyList<(string Path, string Root, MediaKind Kind)> EnumerateFiles(
        IEnumerable<SourceFolder> sources,
        Action<string>? warning,
        CancellationToken cancellationToken,
        IEnumerable<string>? ignoredFolderNames = null)
    {
        var ignored = new HashSet<string>(DefaultIgnoredFolders, StringComparer.OrdinalIgnoreCase);
        foreach (var name in ignoredFolderNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name)) ignored.Add(name.Trim());
        }

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
                        if (!IsIgnoredDirectory(child, ignored) && !IsReparsePoint(child))
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

    private static readonly string[] DefaultIgnoredFolders =
    {
        "$RECYCLE.BIN", "System Volume Information", "_DuplicatesReview", "_NeniFoto",
        // sluzebni slozky Synology - bez nich sken nacte nahledy z @eaDir jako fotografie
        "@eaDir", "#recycle", "#snapshot"
    };

    private static bool IsIgnoredDirectory(string path, HashSet<string> ignored)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.') || ignored.Contains(name);
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
