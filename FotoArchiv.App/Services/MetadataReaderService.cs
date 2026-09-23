using System.Globalization;
using System.Text.RegularExpressions;
using FotoArchiv.App.Models;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace FotoArchiv.App.Services;

public sealed partial class MetadataReaderService
{
    private static readonly string[] PreferredDateTags =
    {
        "Date/Time Original", "Date/Time Digitized", "Create Date", "Creation Time",
        "Media Create Date", "Date/Time"
    };

    public Task<MediaItem> ReadAsync(
        string path,
        string sourceRoot,
        MediaKind kind,
        CancellationToken cancellationToken) =>
        Task.Run(() => Read(path, sourceRoot, kind, cancellationToken), cancellationToken);

    private static MediaItem Read(string path, string sourceRoot, MediaKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = new FileInfo(path);
        var item = new MediaItem
        {
            FilePath = file.FullName,
            SourceRoot = sourceRoot,
            Kind = kind,
            FileSize = file.Length,
            FileCreatedAt = file.CreationTime,
            FileModifiedAt = file.LastWriteTime,
            BundleKey = BuildBundleKey(path)
        };

        if (kind == MediaKind.Sidecar)
        {
            item.CapturedAt = TryParseDateFromFileName(item.FileName, out var sidecarDate) ? sidecarDate : null;
            item.CaptureDateSource = item.CapturedAt is null ? "Neznámé" : "Název souboru";
            return item;
        }

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            foreach (var directory in directories)
            {
                foreach (var tag in directory.Tags.Take(500))
                {
                    if (!string.IsNullOrWhiteSpace(tag.Description))
                    {
                        item.Metadata.Add(new MetadataField(directory.Name, tag.Name, tag.Description));
                    }
                }
            }

            ReadExifFields(directories, item);
            ReadCaptureDate(directories, item);
        }
        catch (Exception exception) when (exception is ImageProcessingException or IOException or UnauthorizedAccessException)
        {
            item.Error = exception.Message;
        }

        if (kind == MediaKind.Image)
        {
            try
            {
                var info = new MagickImageInfo(path);
                item.Width = checked((int)info.Width);
                item.Height = checked((int)info.Height);
                item.Format = info.Format.ToString();
            }
            catch (Exception exception)
            {
                item.Error ??= exception.Message;
            }
        }

        if (item.CapturedAt is null && TryParseDateFromFileName(item.FileName, out var fileNameDate))
        {
            item.CapturedAt = fileNameDate;
            item.CaptureDateSource = "Název souboru";
        }

        if (item.CapturedAt is null)
        {
            item.CapturedAt = item.FileCreatedAt;
            item.CaptureDateSource = "Čas souboru";
        }

        return item;
    }

    private static void ReadExifFields(IReadOnlyList<MetadataExtractor.Directory> directories, MediaItem item)
    {
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        item.CameraMake = ifd0?.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
        item.CameraModel = ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim();

        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        if (gps?.TryGetGeoLocation(out var location) == true)
        {
            item.Latitude = location.Latitude;
            item.Longitude = location.Longitude;
        }
    }

    private static void ReadCaptureDate(IReadOnlyList<MetadataExtractor.Directory> directories, MediaItem item)
    {
        foreach (var preferredName in PreferredDateTags)
        {
            foreach (var directory in directories)
            {
                var tag = directory.Tags.FirstOrDefault(candidate =>
                    candidate.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));

                if (tag is not null && directory.TryGetDateTime(tag.Type, out var date))
                {
                    item.CapturedAt = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local));
                    item.CaptureDateSource = preferredName;
                    return;
                }

                if (tag?.Description is { } value && TryParseMetadataDate(value, out var parsed))
                {
                    item.CapturedAt = parsed;
                    item.CaptureDateSource = preferredName;
                    return;
                }
            }
        }
    }

    private static bool TryParseMetadataDate(string value, out DateTimeOffset result)
    {
        var formats = new[]
        {
            "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm:ssK", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ssK", "ddd MMM dd HH:mm:ss K yyyy"
        };

        return DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out result) ||
               DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out result);
    }

    private static bool TryParseDateFromFileName(string fileName, out DateTimeOffset result)
    {
        var match = FileNameDateRegex().Match(fileName);
        if (match.Success)
        {
            var raw = match.Groups["date"].Value + match.Groups["time"].Value.PadRight(6, '0');
            if (DateTime.TryParseExact(raw, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                result = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
                return true;
            }
        }

        result = default;
        return false;
    }

    private static string BuildBundleKey(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.GetFileNameWithoutExtension(fileName);
        }

        const string supplemental = ".supplemental-metadata";
        if (fileName.EndsWith(supplemental, StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^supplemental.Length];
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, stem).ToUpperInvariant();
    }

    [GeneratedRegex(@"(?<!\d)(?<date>20\d{2}[01]\d[0-3]\d)(?:[_\- ]?(?<time>[0-2]\d[0-5]\d(?:[0-5]\d)?)(?:\d{3})?)?(?!\d)", RegexOptions.Compiled)]
    private static partial Regex FileNameDateRegex();
}
