using FotoArchiv.App.Common;

namespace FotoArchiv.App.Models;

public sealed class OrganizationPlanItem : ObservableObject
{
    private PlannedAction _action;
    private string _status = "Připraveno";

    public required MediaItem Media { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public string? Warning { get; init; }

    public PlannedAction Action
    {
        get => _action;
        set => SetProperty(ref _action, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}
