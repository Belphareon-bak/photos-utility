namespace FotoArchiv.App.Models;

public enum MediaKind
{
    Image,
    Video,
    Sidecar
}

public enum DuplicateConfidence
{
    None = 0,
    Low = 1,
    High = 2,
    Exact = 3
}

public enum PlannedAction
{
    None,
    Copy,
    Move,
    Quarantine,
    Skip,
    Error
}

public enum TransferMode
{
    Copy,
    Move
}
