namespace FotoArchiv.App.Models;

public sealed record PendingOperation(
    long Id,
    long RunId,
    string SourcePath,
    string TargetPath,
    string Action,
    string SourceHash,
    long? UndoOfOperationId,
    string? TemporaryPath);
