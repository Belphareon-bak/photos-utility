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
        "Media Create Date",
        // MetadataExtractor pojmenovava datum v hlavicce QuickTime/MP4 "Created". Bez teto
        // polozky nedostalo datum z metadat zadne video a vsechna spadla na cas souboru.
        "Created",
        "Date/Time"
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
        MediaItem item;
        try
        {
            item = new MediaItem
            {
                FilePath = file.FullName,
                SourceRoot = sourceRoot,
                Kind = kind,
                FileSize = file.Length,
                FileCreatedAt = file.CreationTime,
                FileModifiedAt = file.LastWriteTime,
                BundleKey = BuildBundleKey(path)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Soubor zmizel nebo je zamceny mezi skenem a ctenim - na NAS se to deje.
            // Vyjimka by jinak propadla z Parallel.ForEachAsync v MainViewModel a shodila
            // cely sken. Polozka dostane duvod vyrazeni a pri provadeni skonci chybou,
            // zdroje se nic nedotkne.
            return new MediaItem
            {
                FilePath = file.FullName,
                SourceRoot = sourceRoot,
                Kind = kind,
                BundleKey = BuildBundleKey(path),
                Error = exception.Message,
                RejectionReason = "Soubor nelze otevrit",
                CaptureDateSource = "Neznámé"
            };
        }

        if (kind == MediaKind.Sidecar)
        {
            item.CapturedAt = TryParseDateFromFileName(item.FileName, out var sidecarDate) ? sidecarDate : null;
            item.CaptureDateSource = item.CapturedAt is null ? "Neznámé" : "Název souboru";
            if (item.FileSize == 0) item.RejectionReason = "Soubor je prazdny";
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

            // MagickImageInfo rozpoznava format podle obsahu, ne podle pripony. Kdyz selze
            // nebo nevrati rozmery, neni to obrazek - at uz je poskozeny, nebo jen prejmenovany.
            if (item.Width is not > 0 || item.Height is not > 0)
            {
                item.RejectionReason = "Soubor nelze nacist jako obrazek";
            }
        }

        if (item.FileSize == 0)
        {
            item.RejectionReason = "Soubor je prazdny";
        }

        if (item.CapturedAt is null && TryParseDateFromFileName(item.FileName, out var fileNameDate))
        {
            item.CapturedAt = fileNameDate;
            item.CaptureDateSource = "Název souboru";
        }

        if (item.CapturedAt is null)
        {
            // Kopirovani souboru zachova LastWriteTime, ale CreationTime nastavi na teď.
            // U materialu, ktery byl nekolikrat stehovany, je proto starsi z obou hodnot
            // vyrazne lepsi odhad nez kterakoli z nich zvlast.
            // Retezec "Čas souboru" nemenit - filtruje se podle nej v SidecarMetadataService.
            item.CapturedAt = item.FileModifiedAt < item.FileCreatedAt
                ? item.FileModifiedAt
                : item.FileCreatedAt;
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

    // EXIF uklada mistni cas bez zony, zatimco hlavicka QuickTime/MP4 uklada UTC.
    // Oznacit oboji jako mistni cas posune datum videa o posun zony a zaznam porizeny
    // krátce po pulnoci spadne do slozky predchoziho dne.
    private static bool IsUtcDirectory(string directoryName) =>
        directoryName.Contains("QuickTime", StringComparison.OrdinalIgnoreCase) ||
        directoryName.Contains("MP4", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset BuildCaptureDate(DateTime value, string directoryName) =>
        IsUtcDirectory(directoryName)
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToLocalTime()
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));

    // Formaty kodují "datum neni nastaveno" jako nulu sve epochy: QuickTime 1904-01-01,
    // Unix 1970-01-01, FAT/DOS 1980-01-01. Takove datum neni datum porizeni - bez teto
    // kontroly by video bez data skoncilo ve slozce 1904. Datum z budoucnosti je rozhozeny cas.
    private static readonly DateTime[] EpochZeros =
    {
        new(1904, 1, 1), new(1970, 1, 1), new(1980, 1, 1)
    };

    private static bool IsPlausibleCaptureDate(DateTimeOffset value)
    {
        foreach (var zero in EpochZeros)
        {
            // porovnava se UTC i mistni zapis - nula muze prijit v kterekoli podobe
            if (value.UtcDateTime == zero || value.DateTime == zero) return false;
        }

        return value <= DateTimeOffset.Now.AddDays(1);
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
                    var candidate = BuildCaptureDate(date, directory.Name);
                    if (IsPlausibleCaptureDate(candidate))
                    {
                        item.CapturedAt = candidate;
                        item.CaptureDateSource = preferredName;
                        return;
                    }

                    // nulova hodnota (napr. 1904-01-01 u videa bez data) - zkusit dalsi znacku
                    continue;
                }

                if (tag?.Description is { } value &&
                    TryParseMetadataDate(value, IsUtcDirectory(directory.Name), out var parsed) &&
                    IsPlausibleCaptureDate(parsed))
                {
                    item.CapturedAt = parsed;
                    item.CaptureDateSource = preferredName;
                    return;
                }
            }
        }
    }

    private static bool TryParseMetadataDate(string value, bool assumeUtc, out DateTimeOffset result)
    {
        // hodnota bez zony: z QuickTime/MP4 je to UTC, z EXIF mistni cas
        var style = DateTimeStyles.AllowWhiteSpaces |
                    (assumeUtc ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal);
        var formats = new[]
        {
            "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm:ssK", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ssK", "ddd MMM dd HH:mm:ss K yyyy"
        };

        if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, style, out result) ||
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, style, out result))
        {
            result = result.ToLocalTime();
            return true;
        }

        return false;
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
