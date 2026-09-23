using System.Collections.ObjectModel;
using FotoArchiv.App.Common;

namespace FotoArchiv.App.Models;

public sealed class DuplicateGroup : ObservableObject
{
    private bool _isResolved;

    public required string Id { get; init; }
    public required DuplicateConfidence Confidence { get; init; }
    public required string Reason { get; init; }
    public required ObservableCollection<MediaItem> Items { get; init; }

    public string ConfidenceLabel => Confidence switch
    {
        DuplicateConfidence.Exact => "1 · Absolutní jistota",
        DuplicateConfidence.High => "2 · Vysoká jistota",
        DuplicateConfidence.Low => "3 · Nízká jistota",
        _ => "Bez shody"
    };

    public string Summary => $"{Items.Count} souborů · {Reason}";

    public bool IsResolved
    {
        get => _isResolved;
        set => SetProperty(ref _isResolved, value);
    }
}
