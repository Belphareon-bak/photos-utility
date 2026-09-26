using FotoArchiv.App.Models;
using Microsoft.Data.Sqlite;

namespace FotoArchiv.App.Services;

public sealed class CatalogService
{
    private readonly string _connectionString;
    private readonly string _operationLockPath;

    public CatalogService(string? databasePath = null)
    {
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FotoArchiv", "catalog.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _operationLockPath = databasePath + ".operations.lock";
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
    }

    public FileStream AcquireExclusiveOperationLock() => new(
        _operationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
                undo_of_operation_id INTEGER,
                temp_path TEXT,
                FOREIGN KEY(run_id) REFERENCES runs(id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(operations);";
        var hasUndoReference = false;
        var hasTemporaryPath = false;
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(1) == "undo_of_operation_id") hasUndoReference = true;
                if (reader.GetString(1) == "temp_path") hasTemporaryPath = true;
            }
        }
        if (!hasUndoReference)
        {
            var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE operations ADD COLUMN undo_of_operation_id INTEGER;";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!hasTemporaryPath)
        {
            var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE operations ADD COLUMN temp_path TEXT;";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<long> BeginOperationAsync(
        long runId, string sourcePath, string targetPath, string action, string hash,
        long? undoOfOperationId, string? temporaryPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operations(run_id, source_path, target_path, action, source_hash, status, undo_of_operation_id, temp_path)
            VALUES ($run, $source, $target, $action, $hash, 'Rozpracováno', $undo, $temp);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue("$target", targetPath);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$undo", (object?)undoOfOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$temp", (object?)temporaryPath ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task FinishOperationAsync(long operationId, string status, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE operations SET status = $status, error = $error WHERE id = $id AND status = 'Rozpracováno';";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", operationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Rozpracovaná operace #{operationId} už nemá očekávaný stav.");
    }

    public async Task<IReadOnlyList<PendingOperation>> GetPendingOperationsAsync(CancellationToken cancellationToken)
    {
        var result = new List<PendingOperation>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, run_id, source_path, target_path, action, source_hash, undo_of_operation_id, temp_path
            FROM operations WHERE status = 'Rozpracováno' ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PendingOperation(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
    }

    public async Task CloseInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET status = 'Přerušeno; stav souborů ověřen', completed_at = $completed
            WHERE status IN ('Běží', 'Vyžaduje kontrolu') AND NOT EXISTS (
                SELECT 1 FROM operations WHERE operations.run_id = runs.id AND operations.status = 'Rozpracováno');
            """;
        command.Parameters.AddWithValue("$completed", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkPendingRunsForReviewAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET status = 'Vyžaduje kontrolu'
            WHERE status = 'Běží' AND EXISTS (
                SELECT 1 FROM operations WHERE operations.run_id = runs.id AND operations.status = 'Rozpracováno');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> StartRunAsync(string runType, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_path, target_path, action, source_hash
            FROM operations
            WHERE run_id = $run AND status = 'Hotovo' AND source_hash IS NOT NULL
              AND action IN ('Copy', 'Move', 'Quarantine')
              AND NOT EXISTS (
                  SELECT 1 FROM operations undo
                  WHERE undo.undo_of_operation_id = operations.id AND undo.status = 'Hotovo')
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
