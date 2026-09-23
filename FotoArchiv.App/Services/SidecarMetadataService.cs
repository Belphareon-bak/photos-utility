using System.Globalization;
using System.Text.Json;
using FotoArchiv.App.Models;

namespace FotoArchiv.App.Services;

public sealed class SidecarMetadataService
{
    public async Task ApplyAsync(
        IReadOnlyList<MediaItem> media,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var primaryByBundle = media
            .Where(item => item.Kind != MediaKind.Sidecar)
            .GroupBy(item => item.BundleKey)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var jsonSidecars = media.Where(item => item.Kind == MediaKind.Sidecar && item.Extension == ".json").ToList();
        var completed = 0;

        foreach (var sidecar in jsonSidecars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(sidecar.FilePath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!primaryByBundle.TryGetValue(sidecar.BundleKey, out var primaries))
                {
                    primaries = FindByTitle(document.RootElement, sidecar, media);
                }

                if (primaries.Count > 0)
                {
                    ApplyDate(document.RootElement, sidecar, primaries);
                    ApplyCoordinates(document.RootElement, sidecar, primaries);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                sidecar.Error = exception.Message;
            }

            completed++;
            if (completed % 100 == 0 || completed == jsonSidecars.Count)
                progress?.Report($"Sidecar metadata: {completed} / {jsonSidecars.Count}");
        }
    }

    private static List<MediaItem> FindByTitle(JsonElement root, MediaItem sidecar, IReadOnlyList<MediaItem> media)
    {
        if (!root.TryGetProperty("title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String)
            return [];

        var title = titleElement.GetString();
        var directory = Path.GetDirectoryName(sidecar.FilePath);
        return media.Where(item => item.Kind != MediaKind.Sidecar &&
                                   string.Equals(Path.GetDirectoryName(item.FilePath), directory, StringComparison.OrdinalIgnoreCase) &&
                                   item.FileName.Equals(title, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static void ApplyDate(JsonElement root, MediaItem sidecar, IReadOnlyList<MediaItem> primaries)
    {
        if (!root.TryGetProperty("photoTakenTime", out var takenTime) ||
            !takenTime.TryGetProperty("timestamp", out var timestampElement))
        {
            return;
        }

        var timestampText = timestampElement.ValueKind == JsonValueKind.String
            ? timestampElement.GetString()
            : timestampElement.GetRawText();
        if (!long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) return;

        var capturedAt = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        sidecar.CapturedAt = capturedAt;
        sidecar.CaptureDateSource = "Google Takeout JSON";
        foreach (var primary in primaries.Where(item =>
                     item.CaptureDateSource is "Čas souboru" or "Název souboru" or "Neznámé"))
        {
            primary.CapturedAt = capturedAt;
            primary.CaptureDateSource = "Google Takeout JSON";
            primary.Metadata.Add(new MetadataField("Google Takeout", "Photo taken time", capturedAt.ToString("O")));
        }
    }

    private static void ApplyCoordinates(JsonElement root, MediaItem sidecar, IReadOnlyList<MediaItem> primaries)
    {
        if (!TryReadCoordinates(root, "geoDataExif", out var latitude, out var longitude) &&
            !TryReadCoordinates(root, "geoData", out latitude, out longitude))
        {
            return;
        }

        sidecar.Latitude = latitude;
        sidecar.Longitude = longitude;
        foreach (var primary in primaries.Where(item => item.Latitude is null || item.Longitude is null))
        {
            primary.Latitude = latitude;
            primary.Longitude = longitude;
            primary.Metadata.Add(new MetadataField("Google Takeout", "GPS", $"{latitude:0.######}, {longitude:0.######}"));
        }
    }

    private static bool TryReadCoordinates(JsonElement root, string property, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (!root.TryGetProperty(property, out var geo) ||
            !geo.TryGetProperty("latitude", out var latitudeElement) ||
            !geo.TryGetProperty("longitude", out var longitudeElement) ||
            !latitudeElement.TryGetDouble(out latitude) ||
            !longitudeElement.TryGetDouble(out longitude))
        {
            return false;
        }

        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 &&
               (Math.Abs(latitude) > double.Epsilon || Math.Abs(longitude) > double.Epsilon);
    }
}
