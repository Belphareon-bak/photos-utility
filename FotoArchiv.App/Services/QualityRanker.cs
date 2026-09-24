using FotoArchiv.App.Models;

namespace FotoArchiv.App.Services;

public static class QualityRanker
{
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".arw", ".cr2", ".cr3", ".nef", ".orf", ".raf", ".rw2"
    };

    public static MediaItem SelectBest(IEnumerable<MediaItem> items)
    {
        // RAW vzdy vyhrava. Potom rozliseni a az pak format - jinak zmensene PNG (poradi 5)
        // porazilo originalni JPG (poradi 3) s mnohonasobne vetsim rozlisenim. PNG v telefonnich
        // fotkach skoro vzdy znamena snimek obrazovky nebo export, ne original.
        var ordered = items
            .OrderByDescending(item => RawExtensions.Contains(item.Extension))
            .ThenByDescending(item => item.PixelCount)
            .ThenByDescending(item => FormatRank(item.Extension))
            .ThenByDescending(item => item.BitDepth ?? 0)
            .ThenByDescending(item => item.FileSize)
            .ThenByDescending(item => item.Metadata.Count)
            .ThenBy(item => item.FilePath.Length)
            .ToList();

        foreach (var item in ordered)
        {
            item.IsRecommendedKeep = false;
            item.QualityLabel = Describe(item);
        }

        var best = ordered[0];
        best.IsRecommendedKeep = true;
        return best;
    }

    private static int FormatRank(string extension)
    {
        if (RawExtensions.Contains(extension)) return 6;
        if (extension is ".tif" or ".tiff" or ".png") return 5;
        if (extension is ".heic" or ".heif") return 4;
        if (extension is ".jpg" or ".jpeg" or ".webp") return 3;
        return 1;
    }

    private static string Describe(MediaItem item)
    {
        var format = RawExtensions.Contains(item.Extension) ? "RAW" : (item.Format ?? item.Extension.TrimStart('.')).ToUpperInvariant();
        var depth = item.BitDepth is > 0 ? $", {item.BitDepth} bit" : string.Empty;
        return $"{format}, {item.Dimensions}{depth}, {item.SizeText}";
    }
}
