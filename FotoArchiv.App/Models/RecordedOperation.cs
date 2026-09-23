namespace FotoArchiv.App.Models;

public sealed record RecordedOperation(
    long Id,
    string SourcePath,
    string TargetPath,
    PlannedAction Action,
    string SourceHash);
