using FotoArchiv.App.Common;

namespace FotoArchiv.App.Models;

public sealed class SourceFolder(string path) : ObservableObject
{
    private bool _isEnabled = true;
    private string _path = path;
    private double _captureTimeOffsetHours;

    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public double CaptureTimeOffsetHours
    {
        get => _captureTimeOffsetHours;
        set => SetProperty(ref _captureTimeOffsetHours, value);
    }

    public string DisplayName => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Path;
}
