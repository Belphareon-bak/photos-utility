using System.Collections.ObjectModel;
using FotoArchiv.App.Common;

namespace FotoArchiv.App.Models;

public sealed class MediaItem : ObservableObject
{
    private string _filePath = string.Empty;
    private DateTimeOffset? _capturedAt;
    private string _captureDateSource = "Neznámé";
    private string? _locationName;
    private string? _countryCode;
    private string? _regionName;
    private DuplicateConfidence _duplicateConfidence;
    private string? _duplicateGroupId;
    private bool _isRecommendedKeep;
    private bool _willKeep = true;
    private string _status = "Připraveno";
    private string? _sha256;
    private ulong? _perceptualHash;
    private string? _normalizedPixelHash;
    private string? _targetPath;
    private string _qualityLabel = "Nezjištěno";

    public required string SourceRoot { get; init; }
    public required MediaKind Kind { get; init; }
    public long FileSize { get; init; }
    public DateTimeOffset FileCreatedAt { get; init; }
    public DateTimeOffset FileModifiedAt { get; init; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? BitDepth { get; set; }
    public string? Format { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Error { get; set; }
    public string BundleKey { get; set; } = string.Empty;
    public ObservableCollection<MetadataField> Metadata { get; } = [];

    public string FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(Extension));
            }
        }
    }

    public string FileName => Path.GetFileName(FilePath);
    public string Extension => Path.GetExtension(FilePath).ToLowerInvariant();
    public long PixelCount => (long)(Width ?? 0) * (Height ?? 0);
    public string Dimensions => Width is > 0 && Height is > 0 ? $"{Width} × {Height}" : "—";
    public string SizeText => FileSize switch
    {
        >= 1_073_741_824 => $"{FileSize / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{FileSize / 1_048_576d:0.0} MB",
        >= 1024 => $"{FileSize / 1024d:0.0} kB",
        _ => $"{FileSize} B"
    };

    public DateTimeOffset? CapturedAt
    {
        get => _capturedAt;
        set => SetProperty(ref _capturedAt, value);
    }

    public string CaptureDateSource
    {
        get => _captureDateSource;
        set => SetProperty(ref _captureDateSource, value);
    }

    public string? LocationName
    {
        get => _locationName;
        set
        {
            if (SetProperty(ref _locationName, value))
            {
                OnPropertyChanged(nameof(LocationDisplay));
            }
        }
    }

    public string? CountryCode
    {
        get => _countryCode;
        set => SetProperty(ref _countryCode, value);
    }

    public string? RegionName
    {
        get => _regionName;
        set => SetProperty(ref _regionName, value);
    }

    public string LocationDisplay => LocationName ?? (Latitude is not null && Longitude is not null
        ? $"GPS {Latitude:0.0000}, {Longitude:0.0000}"
        : "Bez GPS");

    public string? Sha256
    {
        get => _sha256;
        set => SetProperty(ref _sha256, value);
    }

    public ulong? PerceptualHash
    {
        get => _perceptualHash;
        set => SetProperty(ref _perceptualHash, value);
    }

    public string? NormalizedPixelHash
    {
        get => _normalizedPixelHash;
        set => SetProperty(ref _normalizedPixelHash, value);
    }

    public DuplicateConfidence DuplicateConfidence
    {
        get => _duplicateConfidence;
        set => SetProperty(ref _duplicateConfidence, value);
    }

    public string? DuplicateGroupId
    {
        get => _duplicateGroupId;
        set => SetProperty(ref _duplicateGroupId, value);
    }

    public bool IsRecommendedKeep
    {
        get => _isRecommendedKeep;
        set => SetProperty(ref _isRecommendedKeep, value);
    }

    public bool WillKeep
    {
        get => _willKeep;
        set => SetProperty(ref _willKeep, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string? TargetPath
    {
        get => _targetPath;
        set => SetProperty(ref _targetPath, value);
    }

    public string QualityLabel
    {
        get => _qualityLabel;
        set => SetProperty(ref _qualityLabel, value);
    }
}
