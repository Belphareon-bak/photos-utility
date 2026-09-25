using FotoArchiv.App.Models;

namespace FotoArchiv.App.Services;

public sealed class OrganizationPlanner
{
    public IReadOnlyList<OrganizationPlanItem> Build(
        IReadOnlyList<MediaItem> media,
        AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DestinationRoot))
        {
            throw new InvalidOperationException("Nejdříve vyberte cílovou složku.");
        }

        var destinationRoot = Path.GetFullPath(settings.DestinationRoot);
        var active = media.Where(item => item.WillKeep).ToList();

        // Soubory, ktere nejsou pouzitelna fotografie ani video, do archivu nepatri.
        // Odkladaji se stranou a nesmi vstoupit do cislovani, jinak by v rade 001, 002…
        // vznikaly diry. Sidecar odchazi stranou spolu se svym primarnim souborem.
        var rejectedBundles = active
            .Where(item => item.Kind != MediaKind.Sidecar && item.RejectionReason is not null)
            .Select(item => item.BundleKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejected = active
            .Where(item => item.RejectionReason is not null ||
                           (item.Kind == MediaKind.Sidecar && rejectedBundles.Contains(item.BundleKey)))
            .ToList();
        var rejectedSet = rejected.ToHashSet();
        var usable = active.Where(item => !rejectedSet.Contains(item)).ToList();
        var primaries = usable.Where(item => item.Kind != MediaKind.Sidecar).ToList();
        var sidecars = usable.Where(item => item.Kind == MediaKind.Sidecar).ToList();
        var bundleTargets = new Dictionary<string, (string Directory, string BaseName, MediaItem Representative)>();
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<OrganizationPlanItem>();
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sidecarRedirects = BuildExactDuplicateRedirects(media);

        foreach (var bundle in primaries
                     .GroupBy(item => item.BundleKey)
                     .OrderBy(group => group.Min(item => item.CapturedAt))
                     .ThenBy(group => group.Key))
        {
            var representative = bundle
                .OrderBy(item => item.Kind == MediaKind.Image ? 0 : 1)
                .ThenByDescending(item => item.PixelCount)
                .First();
            var relativeDirectory = BuildRelativeDirectory(representative, settings);
            var targetDirectory = Path.Combine(destinationRoot, relativeDirectory);
            counters.TryGetValue(targetDirectory, out var sequence);
            sequence++;

            string baseName;
            while (true)
            {
                baseName = sequence.ToString(sequence <= 999 ? "000" : "0000");
                var proposed = bundle.Select(item => Path.Combine(targetDirectory, baseName + item.Extension)).ToList();
                if (proposed.All(path => !File.Exists(path) && !usedTargets.Contains(path)))
                {
                    break;
                }

                sequence++;
            }

            counters[targetDirectory] = sequence;
            bundleTargets[bundle.Key] = (targetDirectory, baseName, representative);

            foreach (var item in bundle)
            {
                var target = Path.Combine(targetDirectory, baseName + item.Extension);
                usedTargets.Add(target);
                item.TargetPath = target;
                plans.Add(CreatePlan(item, target, settings.TransferMode));
            }
        }

        // Sidecary ponechanych souboru dostanou bezny nazev. Sidecary
        // vyrazenych bajtovych kopii nasleduji az po nich.
        foreach (var sidecar in sidecars
                     .OrderBy(item => bundleTargets.ContainsKey(item.BundleKey) ? 0 : 1)
                     .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!bundleTargets.TryGetValue(sidecar.BundleKey, out var targetInfo) &&
                !(sidecarRedirects.TryGetValue(sidecar.BundleKey, out var keptBundle) &&
                  bundleTargets.TryGetValue(keptBundle, out targetInfo)))
            {
                plans.Add(new OrganizationPlanItem
                {
                    Media = sidecar,
                    SourcePath = sidecar.FilePath,
                    TargetPath = sidecar.FilePath,
                    Action = PlannedAction.Skip,
                    Warning = "Sidecar nemá odpovídající fotografii nebo video."
                });
                continue;
            }

            var suffix = GetSidecarSuffix(sidecar.FileName, targetInfo.Representative.Extension);
            var target = Path.Combine(targetInfo.Directory, targetInfo.BaseName + suffix);
            string? warning = null;
            if (File.Exists(target) || usedTargets.Contains(target))
            {
                // Dva sidecary mohou obsahovat ruzne upravy tehoz obrazku.
                // Zachovat oba, ale druhemu dat odlisny nazev a upozornit,
                // ze je treba rozhodnout o slouceni metadat.
                target = MakeUnique(target, usedTargets);
                warning = "Další sidecar shodné fotografie má odlišný název; zkontrolujte sloučení metadat.";
            }

            usedTargets.Add(target);
            sidecar.TargetPath = target;
            plans.Add(CreatePlan(sidecar, target, settings.TransferMode, warning));
        }

        foreach (var item in rejected)
        {
            var target = MakeUnique(
                Path.Combine(destinationRoot, SanitizeSegment(settings.RejectedFolderName), SafeRelativePath(item)),
                usedTargets);
            usedTargets.Add(target);
            item.TargetPath = target;
            plans.Add(new OrganizationPlanItem
            {
                Media = item,
                SourcePath = item.FilePath,
                TargetPath = target,
                Action = settings.TransferMode == TransferMode.Copy ? PlannedAction.Copy : PlannedAction.Move,
                Warning = item.RejectionReason ?? "Soubor neni pouzitelna fotografie ani video."
            });
        }

        return plans.OrderBy(plan => plan.TargetPath).ToList();
    }

    public IReadOnlyList<OrganizationPlanItem> BuildQuarantinePlan(IReadOnlyList<MediaItem> media, AppSettings settings)
    {
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<OrganizationPlanItem>();
        // Pri opakovani po castecne chybe znovu zpracovat jen soubory,
        // ktere jeste nebyly uspesne presunuty do karanteny.
        var selected = media.Where(item => !item.WillKeep && !IsAlreadyQuarantined(item, settings)).ToHashSet();
        var removedBundles = selected.Where(item => item.Kind != MediaKind.Sidecar).Select(item => item.BundleKey).ToHashSet();
        var sidecarRedirects = BuildExactDuplicateRedirects(media);
        foreach (var sidecar in media.Where(item => item.Kind == MediaKind.Sidecar && removedBundles.Contains(item.BundleKey)))
        {
            var hasKeptPrimary = media.Any(item => item.Kind != MediaKind.Sidecar && item.BundleKey == sidecar.BundleKey && item.WillKeep);
            // sidecar bajtove kopie zustava - pri organizaci se pripoji k ponechane kopii
            if (!hasKeptPrimary && !sidecarRedirects.ContainsKey(sidecar.BundleKey) &&
                !IsAlreadyQuarantined(sidecar, settings))
            {
                sidecar.WillKeep = false;
                selected.Add(sidecar);
            }
        }

        foreach (var item in selected)
        {
            var confidenceFolder = item.DuplicateConfidence switch
            {
                DuplicateConfidence.Exact => "1_Exact",
                DuplicateConfidence.High => "2_High",
                _ => "3_Review"
            };
            var target = Path.Combine(item.SourceRoot, SanitizeSegment(settings.QuarantineFolderName),
                confidenceFolder, SafeRelativePath(item));
            target = MakeUnique(target, usedTargets);
            usedTargets.Add(target);
            plans.Add(new OrganizationPlanItem
            {
                Media = item,
                SourcePath = item.FilePath,
                TargetPath = target,
                Action = PlannedAction.Quarantine
            });
        }

        return plans;
    }

    private static bool IsAlreadyQuarantined(MediaItem item, AppSettings settings)
    {
        if (item.Status != "Hotovo") return false;
        var root = Path.GetFullPath(Path.Combine(item.SourceRoot, SanitizeSegment(settings.QuarantineFolderName)))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var current = Path.GetFullPath(item.FilePath);
        return current.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Neznámé" : sanitized;
    }

    private static string BuildRelativeDirectory(MediaItem item, AppSettings settings)
    {
        var segments = new List<string>();
        if (settings.IncludeCountry && !string.IsNullOrWhiteSpace(item.CountryCode))
            segments.Add(SanitizeSegment(item.CountryCode));
        if (settings.IncludeRegion && !string.IsNullOrWhiteSpace(item.RegionName))
            segments.Add(SanitizeSegment(item.RegionName));

        segments.Add(SanitizeSegment(item.LocationName ?? "Bez_GPS"));
        if (settings.SplitLocationByDate)
        {
            segments.Add(item.CapturedAt?.ToString("yyyy-MM-dd") ?? "Bez_data");
        }

        return Path.Combine(segments.ToArray());
    }

    private static OrganizationPlanItem CreatePlan(MediaItem item, string target, TransferMode mode, string? warning = null) => new()
    {
        Media = item,
        SourcePath = item.FilePath,
        TargetPath = target,
        Action = mode == TransferMode.Copy ? PlannedAction.Copy : PlannedAction.Move,
        Warning = warning ?? (target.Length > 240 ? "Dlouhá cílová cesta; ověřte podporu dlouhých cest na NAS." : null)
    };

    private static string GetSidecarSuffix(string fileName, string primaryExtension)
    {
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            fileName[..^5].EndsWith(primaryExtension, StringComparison.OrdinalIgnoreCase))
        {
            return primaryExtension + ".json";
        }

        return Path.GetExtension(fileName).ToLowerInvariant();
    }

    // Sidecar (xmp, aae, json) patri ke konkretnimu souboru. Kdyz se ten soubor jako
    // BAJTOVA kopie neponecha, plati sidecar stejne pro ponechanou kopii - obsah je
    // identicky. Driv takovy sidecar skoncil v karantene s vyrazenou kopii a v archivu
    // chybely upravy a hodnoceni. U jen podobnych snimku (High/Low) se nepresmerovava:
    // tam by upravy patrily k jinemu souboru.
    // Vraci: BundleKey vyrazene kopie -> BundleKey ponechane kopie.
    private static Dictionary<string, string> BuildExactDuplicateRedirects(IReadOnlyList<MediaItem> media)
    {
        static bool IsExactPrimary(MediaItem item) =>
            item.Kind != MediaKind.Sidecar && item.DuplicateGroupId is not null &&
            item.DuplicateConfidence == DuplicateConfidence.Exact;

        var keptByGroup = media
            .Where(item => IsExactPrimary(item) && item.WillKeep)
            .GroupBy(item => item.DuplicateGroupId!)
            .ToDictionary(group => group.Key, group => group.First().BundleKey);
        var keptBundles = media
            .Where(item => item.Kind != MediaKind.Sidecar && item.WillKeep)
            .Select(item => item.BundleKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dropped in media.Where(item => IsExactPrimary(item) && !item.WillKeep))
        {
            // kdyz ze stejneho svazku (napr. live photo HEIC + MOV) neco zustava, sidecar zustava u nej
            if (keptBundles.Contains(dropped.BundleKey)) continue;
            if (keptByGroup.TryGetValue(dropped.DuplicateGroupId!, out var kept))
            {
                redirects.TryAdd(dropped.BundleKey, kept);
            }
        }

        return redirects;
    }

    // Path.GetRelativePath vraci segmenty ".." kdyz SourceRoot neni predkem FilePath.
    // Path.Combine by pak vysledek vyvedl mimo urcenou slozku, takze se v tom pripade
    // vraci jen jmeno souboru.
    private static string SafeRelativePath(MediaItem item)
    {
        try
        {
            var relative = Path.GetRelativePath(item.SourceRoot, item.FilePath);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative;
            }
        }
        catch (ArgumentException)
        {
            // neplatna cesta - spadne na jmeno souboru nize
        }

        return item.FileName;
    }

    private static string MakeUnique(string path, HashSet<string> used)
    {
        if (!File.Exists(path) && !used.Contains(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var number = 2;
        do
        {
            path = Path.Combine(directory, $"{name}_{number++}{extension}");
        } while (File.Exists(path) || used.Contains(path));
        return path;
    }
}
