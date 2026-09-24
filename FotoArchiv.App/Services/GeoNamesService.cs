using System.Globalization;
using System.IO.Compression;
using FotoArchiv.App.Models;

namespace FotoArchiv.App.Services;

public sealed class GeoNamesService
{
    private readonly string _dataDirectory;
    private readonly Dictionary<(int Lat, int Lon), List<GeoPlace>> _grid = [];
    private readonly Dictionary<string, string> _regions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int Lat, int Lon), GeoPlace?> _cache = [];
    private bool _loaded;

    public GeoNamesService(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "Data");
    }

    public bool IsAvailable => Directory.Exists(_dataDirectory) && Directory.EnumerateFiles(_dataDirectory, "*.zip").Any();

    public async Task ResolveAsync(
        IReadOnlyList<MediaItem> media,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            progress?.Report("GeoNames data nejsou dostupná; lokality lze doplnit ručně.");
            return;
        }

        await EnsureLoadedAsync(progress, cancellationToken);
        // 0, 0 je "bez polohy" (telefon bez signalu), ne skutecne misto - nejblizsi obec
        // k tomu bodu lezi v Ghane. Sidecar z Google Takeout tuhle hodnotu uz odmita,
        // GPS z EXIF ne.
        var located = media
            .Where(item => item.Latitude.HasValue && item.Longitude.HasValue &&
                           !(Math.Abs(item.Latitude.Value) < 1e-6 && Math.Abs(item.Longitude.Value) < 1e-6))
            .ToList();
        var completed = 0;

        foreach (var item in located)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var place = FindNearest(item.Latitude!.Value, item.Longitude!.Value);
            if (place is not null)
            {
                item.LocationName = place.Name;
                item.CountryCode = place.CountryCode;
                item.RegionName = _regions.GetValueOrDefault($"{place.CountryCode}.{place.Admin1Code}");
            }

            completed++;
            if (completed % 100 == 0 || completed == located.Count)
            {
                progress?.Report($"Geolokace: {completed} / {located.Count}");
            }
        }
    }

    private async Task EnsureLoadedAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_loaded) return;

        await Task.Run(() =>
        {
            LoadRegions();
            var knownIds = new HashSet<long>();
            var archives = Directory.EnumerateFiles(_dataDirectory, "*.zip")
                .OrderBy(path => Path.GetFileName(path).StartsWith("cities", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();

            foreach (var archivePath in archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Načítám lokality: {Path.GetFileName(archivePath)}");
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var entry in archive.Entries.Where(entry => entry.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                {
                    using var reader = new StreamReader(entry.Open());
                    while (reader.ReadLine() is { } line)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var parts = line.Split('\t');
                        // Jen trida P - obce, mesta a jejich casti. Podrobna data zemi (CZ.zip)
                        // obsahuji i budovy, komíny, rozvodny, hotely, kopce a regiony; bez filtru
                        // dostala fotka z Krce slozku "[Praha-Krč] heat plant chimney"
                        // a jina slozku "Čechy" podle stredoveho bodu regionu.
                        if (parts.Length < 15 || parts[6] != "P" ||
                            !long.TryParse(parts[0], out var id) || !knownIds.Add(id) ||
                            !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                            !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                        {
                            continue;
                        }

                        var place = new GeoPlace(parts[1], parts[8], parts[10], latitude, longitude,
                            long.TryParse(parts[14], out var population) ? population : 0);
                        var key = ((int)Math.Floor(latitude), (int)Math.Floor(longitude));
                        if (!_grid.TryGetValue(key, out var bucket))
                        {
                            bucket = [];
                            _grid[key] = bucket;
                        }

                        bucket.Add(place);
                    }
                }
            }

            _loaded = true;
        }, cancellationToken);
    }

    private void LoadRegions()
    {
        var path = Path.Combine(_dataDirectory, "admin1CodesASCII.txt");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2) _regions[parts[0]] = parts[1];
        }
    }

    private const double MaxPlaceDistanceKm = 50;

    private GeoPlace? FindNearest(double latitude, double longitude)
    {
        var cacheKey = ((int)Math.Round(latitude * 1000), (int)Math.Round(longitude * 1000));
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        GeoPlace? nearest = null;
        var nearestDistance = double.MaxValue;
        var centerLat = (int)Math.Floor(latitude);
        var centerLon = (int)Math.Floor(longitude);

        foreach (var radius in new[] { 1, 2, 5, 10 })
        {
            for (var lat = centerLat - radius; lat <= centerLat + radius; lat++)
            {
                for (var lon = centerLon - radius; lon <= centerLon + radius; lon++)
                {
                    if (!TryGetBucket(lat, lon, out var bucket)) continue;
                    foreach (var place in bucket)
                    {
                        var distance = Haversine(latitude, longitude, place.Latitude, place.Longitude);
                        if (distance < nearestDistance ||
                            Math.Abs(distance - nearestDistance) < 0.2 && place.Population > (nearest?.Population ?? 0))
                        {
                            nearest = place;
                            nearestDistance = distance;
                        }
                    }
                }
            }

            if (nearest is not null) break;
        }

        // Hledani jde az do okruhu 10 stupnu (pres 1 000 km). Fotka z letadla nebo z lodi
        // by jinak dostala jmeno mista, ktere s ni nesouvisi; radeji zadna lokalita.
        if (nearestDistance > MaxPlaceDistanceKm) nearest = null;

        _cache[cacheKey] = nearest;
        return nearest;
    }

    private bool TryGetBucket(int lat, int lon, out List<GeoPlace> bucket) =>
        _grid.TryGetValue((lat, lon), out bucket!);

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0088;
        var deltaLat = DegreesToRadians(lat2 - lat1);
        var deltaLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Pow(Math.Sin(deltaLat / 2), 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Pow(Math.Sin(deltaLon / 2), 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private sealed record GeoPlace(
        string Name,
        string CountryCode,
        string Admin1Code,
        double Latitude,
        double Longitude,
        long Population);
}
