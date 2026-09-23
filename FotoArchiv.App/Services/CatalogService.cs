using FotoArchiv.App.Models;
using Microsoft.Data.Sqlite;

namespace FotoArchiv.App.Services;

public sealed class CatalogService
{
    private readonly string _connectionString;

    public CatalogService(string? databasePath = null)
    {
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FotoArchiv", "catalog.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_type TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT,
                status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS operations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                source_path TEXT NOT NULL,
                target_path TEXT NOT NULL,
                action TEXT NOT NULL,
                source_hash TEXT,
                status TEXT NOT NULL,
                error TEXT,
                FOREIGN KEY(run_id) REFERENCES runs(id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> StartRunAsync(string runType, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO runs(run_type, started_at, status) VALUES ($type, $started, 'Běží'); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$type", runType);
        command.Parameters.AddWithValue("$started", DateTimeOffset.Now.ToString("O"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task RecordOperationAsync(
        long runId,
        OrganizationPlanItem item,
        string? hash,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operations(run_id, source_path, target_path, action, source_hash, status, error)
            VALUES ($run, $source, $target, $action, $hash, $status, $error);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$source", item.SourcePath);
        command.Parameters.AddWithValue("$target", item.TargetPath);
        command.Parameters.AddWithValue("$action", item.Action.ToString());
        command.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteRunAsync(long runId, string status, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET completed_at = $completed, status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$completed", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecordedOperation>> GetCompletedOperationsAsync(
        long runId,
        CancellationToken cancellationToken)
    {
        var operations = new List<RecordedOperation>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_path, target_path, action, source_hash
            FROM operations
            WHERE run_id = $run AND status = 'Hotovo' AND source_hash IS NOT NULL
            ORDER BY id DESC;
            """;
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Enum.TryParse<PlannedAction>(reader.GetString(3), out var action))
            {
                operations.Add(new RecordedOperation(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), action, reader.GetString(4)));
            }
        }

        return operations;
    }

    public async Task RecordRawOperationAsync(
        long runId,
        string sourcePath,
        string targetPath,
        string action,
        string? hash,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operations(run_id, source_path, target_path, action, source_hash, status, error)
            VALUES ($run, $source, $target, $action, $hash, $status, $error);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue("$target", targetPath);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationHistoryItem>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var history = new List<OperationHistoryItem>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.run_type, r.started_at, r.completed_at, r.status,
                   SUM(CASE WHEN o.status = 'Hotovo' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN o.status = 'Chyba' THEN 1 ELSE 0 END)
            FROM runs r LEFT JOIN operations o ON o.run_id = r.id
            GROUP BY r.id ORDER BY r.id DESC LIMIT 100;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new OperationHistoryItem(
                reader.GetInt64(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6), reader.GetString(4)));
        }

        return history;
    }
}
