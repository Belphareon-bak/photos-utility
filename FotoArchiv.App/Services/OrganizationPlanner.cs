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
        var primaries = active.Where(item => item.Kind != MediaKind.Sidecar).ToList();
        var sidecars = active.Where(item => item.Kind == MediaKind.Sidecar).ToList();
        var bundleTargets = new Dictionary<string, (string Directory, string BaseName, MediaItem Representative)>();
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<OrganizationPlanItem>();
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

        foreach (var sidecar in sidecars)
        {
            if (!bundleTargets.TryGetValue(sidecar.BundleKey, out var targetInfo))
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
            if (File.Exists(target) || !usedTargets.Add(target))
            {
                plans.Add(new OrganizationPlanItem
                {
                    Media = sidecar,
                    SourcePath = sidecar.FilePath,
                    TargetPath = target,
                    Action = PlannedAction.Error,
                    Warning = "Cílový sidecar již existuje."
                });
                continue;
            }

            sidecar.TargetPath = target;
            plans.Add(CreatePlan(sidecar, target, settings.TransferMode));
        }

        return plans.OrderBy(plan => plan.TargetPath).ToList();
    }

    public IReadOnlyList<OrganizationPlanItem> BuildQuarantinePlan(IReadOnlyList<MediaItem> media, AppSettings settings)
    {
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<OrganizationPlanItem>();
        var selected = media.Where(item => !item.WillKeep).ToHashSet();
        var removedBundles = selected.Where(item => item.Kind != MediaKind.Sidecar).Select(item => item.BundleKey).ToHashSet();
        foreach (var sidecar in media.Where(item => item.Kind == MediaKind.Sidecar && removedBundles.Contains(item.BundleKey)))
        {
            var hasKeptPrimary = media.Any(item => item.Kind != MediaKind.Sidecar && item.BundleKey == sidecar.BundleKey && item.WillKeep);
            if (!hasKeptPrimary)
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
            var relative = Path.GetRelativePath(item.SourceRoot, item.FilePath);
            var target = Path.Combine(item.SourceRoot, settings.QuarantineFolderName, confidenceFolder, relative);
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

    private static OrganizationPlanItem CreatePlan(MediaItem item, string target, TransferMode mode) => new()
    {
        Media = item,
        SourcePath = item.FilePath,
        TargetPath = target,
        Action = mode == TransferMode.Copy ? PlannedAction.Copy : PlannedAction.Move,
        Warning = target.Length > 240 ? "Dlouhá cílová cesta; ověřte podporu dlouhých cest na NAS." : null
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
