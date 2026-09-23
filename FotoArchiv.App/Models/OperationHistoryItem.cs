namespace FotoArchiv.App.Models;

public sealed record OperationHistoryItem(
    long RunId,
    string RunType,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int CompletedCount,
    int ErrorCount,
    string Status);
