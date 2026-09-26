using FotoArchiv.App.Models;
using FotoArchiv.App.Services;
using Microsoft.Data.Sqlite;

namespace FotoArchiv.Tests;

public sealed class JournalRecoveryTests
{
    [Fact]
    public async Task Move_LogFailureAfterSourceDeletion_IsRecoveredAndCanBeUndone()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/photo.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var dbPath = directory.File("state/catalog.db");
        var catalog = new CatalogService(dbPath);
        await catalog.InitializeAsync();
        await SetFailureTriggerAsync(dbPath, "NEW.status = 'Hotovo'");
        var executor = new OperationExecutor(catalog);
        var plan = Plan(source, target, directory.Path, PlannedAction.Move);

        await Assert.ThrowsAsync<SqliteException>(() =>
            executor.ExecuteAsync([plan], "Test failed catalog", null, CancellationToken.None));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(target));
        Assert.Single(await catalog.GetPendingOperationsAsync(CancellationToken.None));

        await DropFailureTriggerAsync(dbPath);
        await executor.RecoverPendingAsync(CancellationToken.None);
        Assert.Empty(await catalog.GetPendingOperationsAsync(CancellationToken.None));
        var run = Assert.Single(await catalog.GetHistoryAsync(CancellationToken.None));
        Assert.Equal("Přerušeno; stav souborů ověřen", run.Status);
        Assert.Single(await catalog.GetCompletedOperationsAsync(run.RunId, CancellationToken.None));

        var undo = await executor.UndoAsync(run.RunId, null, CancellationToken.None);
        Assert.Equal(0, undo.ErrorCount);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Copy_LogFailureAfterTargetPublication_IsRecovered()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/photo.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var dbPath = directory.File("state/catalog.db");
        var catalog = new CatalogService(dbPath);
        await catalog.InitializeAsync();
        await SetFailureTriggerAsync(dbPath, "NEW.status = 'Hotovo'");
        var executor = new OperationExecutor(catalog);

        await Assert.ThrowsAsync<SqliteException>(() =>
            executor.ExecuteAsync([Plan(source, target, directory.Path, PlannedAction.Copy)],
                "Test failed copy record", null, CancellationToken.None));
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(target));

        await DropFailureTriggerAsync(dbPath);
        await executor.RecoverPendingAsync(CancellationToken.None);
        Assert.Empty(await catalog.GetPendingOperationsAsync(CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task Undo_LogFailureAfterRestoration_IsRecoveredWithoutSecondUndo()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/photo.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var dbPath = directory.File("state/catalog.db");
        var catalog = new CatalogService(dbPath);
        var executor = new OperationExecutor(catalog);
        var runId = await executor.ExecuteAsync([Plan(source, target, directory.Path, PlannedAction.Move)],
            "Test undo recovery", null, CancellationToken.None);
        await SetFailureTriggerAsync(dbPath, "NEW.status = 'Hotovo' AND OLD.action = 'UndoMove'");

        await Assert.ThrowsAsync<SqliteException>(() => executor.UndoAsync(runId, null, CancellationToken.None));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
        Assert.Single(await catalog.GetPendingOperationsAsync(CancellationToken.None));

        await DropFailureTriggerAsync(dbPath);
        await executor.RecoverPendingAsync(CancellationToken.None);
        Assert.Empty(await catalog.GetPendingOperationsAsync(CancellationToken.None));
        Assert.Empty(await catalog.GetCompletedOperationsAsync(runId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.UndoAsync(runId, null, CancellationToken.None));
    }

    [Fact]
    public async Task AmbiguousMove_LeavesBothFilesAndBlocksNextRun()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/photo.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllBytesAsync(target, [1, 2, 3]);
        var catalog = new CatalogService(directory.File("state/catalog.db"));
        await catalog.InitializeAsync();
        var runId = await catalog.StartRunAsync("Test ambiguous", CancellationToken.None);
        var hash = await HashService.ComputeSha256Async(source, CancellationToken.None);
        await catalog.BeginOperationAsync(runId, source, target, "Move", hash, null, null, CancellationToken.None);

        var executor = new OperationExecutor(catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RecoverPendingAsync(CancellationToken.None));

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(target));
        Assert.Single(await catalog.GetPendingOperationsAsync(CancellationToken.None));
        Assert.Equal("Vyžaduje kontrolu", Assert.Single(await catalog.GetHistoryAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task InterruptedCopy_CleansOnlyItsRecordedTemporaryFile()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/photo.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var partial = target + $".fotoarchiv-{Guid.NewGuid():N}.partial";
        var catalog = new CatalogService(directory.File("state/catalog.db"));
        await catalog.InitializeAsync();
        var runId = await catalog.StartRunAsync("Přerušená kopie", CancellationToken.None);
        var hash = await HashService.ComputeSha256Async(source, CancellationToken.None);
        await catalog.BeginOperationAsync(runId, source, target, "Copy", hash, null, partial, CancellationToken.None);
        await File.WriteAllBytesAsync(partial, [1, 2]);

        await new OperationExecutor(catalog).RecoverPendingAsync(CancellationToken.None);

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(partial));
        Assert.Empty(await catalog.GetPendingOperationsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExistingCatalog_IsMigratedWithoutLosingHistory()
    {
        using var directory = new TestDirectory();
        var dbPath = directory.File("state/catalog.db");
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE runs (id INTEGER PRIMARY KEY AUTOINCREMENT, run_type TEXT NOT NULL,
                    started_at TEXT NOT NULL, completed_at TEXT, status TEXT NOT NULL);
                CREATE TABLE operations (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL,
                    source_path TEXT NOT NULL, target_path TEXT NOT NULL, action TEXT NOT NULL,
                    source_hash TEXT, status TEXT NOT NULL, error TEXT);
                INSERT INTO runs(run_type, started_at, status)
                    VALUES ('Historický běh', '2026-09-25T00:00:00+00:00', 'Hotovo');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var catalog = new CatalogService(dbPath);
        await catalog.InitializeAsync();
        Assert.Equal("Historický běh", Assert.Single(await catalog.GetHistoryAsync(CancellationToken.None)).RunType);
        var runId = await catalog.StartRunAsync("Nový běh", CancellationToken.None);
        await catalog.BeginOperationAsync(runId, directory.File("a"), directory.File("b"),
            "Copy", new string('A', 64), null, null, CancellationToken.None);
        Assert.Single(await catalog.GetPendingOperationsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentExecutor_IsRejectedBeforeChangingFiles()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/photo.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var catalog = new CatalogService(directory.File("state/catalog.db"));
        using var operationLock = catalog.AcquireExclusiveOperationLock();
        var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));

        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(
            [Plan(source, target, directory.Path, PlannedAction.Move)], "Concurrent", null, CancellationToken.None));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    private static OrganizationPlanItem Plan(string source, string target, string root, PlannedAction action) => new()
    {
        Media = TestMedia.Create(source, root), SourcePath = source, TargetPath = target, Action = action
    };

    private static async Task SetFailureTriggerAsync(string dbPath, string condition)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_finish BEFORE UPDATE OF status ON operations WHEN {condition} BEGIN SELECT RAISE(FAIL, 'simulated catalog write failure'); END;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropFailureTriggerAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER fail_finish;";
        await command.ExecuteNonQueryAsync();
    }
}
