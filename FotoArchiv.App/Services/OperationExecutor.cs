using FotoArchiv.App.Models;

namespace FotoArchiv.App.Services;

public sealed class OperationExecutor(CatalogService catalog)
{
    public async Task<long> ExecuteAsync(
        IReadOnlyList<OrganizationPlanItem> plan,
        string runType,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        await catalog.InitializeAsync(cancellationToken);
        var runId = await catalog.StartRunAsync(runType, cancellationToken);
        var completed = 0;
        var errors = 0;
        var executablePlan = plan.Where(item => item.Action is not PlannedAction.Skip and not PlannedAction.Error).ToList();

        try
        {
            foreach (var item in executablePlan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? temporaryPath = null;

                try
                {
                    if (!File.Exists(item.SourcePath))
                        throw new FileNotFoundException("Zdrojový soubor již není dostupný.", item.SourcePath);
                    if (File.Exists(item.TargetPath))
                        throw new IOException("Cílový soubor již existuje; nebyl přepsán.");

                    Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                    temporaryPath = item.TargetPath + $".fotoarchiv-{Guid.NewGuid():N}.partial";
                    await CopyAsync(item.SourcePath, temporaryPath, cancellationToken);

                    var sourceHash = item.Media.Sha256 ?? await HashService.ComputeSha256Async(item.SourcePath, cancellationToken);
                    var targetHash = await HashService.ComputeSha256Async(temporaryPath, cancellationToken);
                    if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Kontrolní součet kopie nesouhlasí se zdrojem.");

                    File.Move(temporaryPath, item.TargetPath, false);
                    temporaryPath = null;

                    if (item.Action is PlannedAction.Move or PlannedAction.Quarantine)
                    {
                        try
                        {
                            File.Delete(item.SourcePath);
                        }
                        catch
                        {
                            // The verified target was created by this operation; remove it to keep a failed move atomic.
                            File.Delete(item.TargetPath);
                            throw;
                        }
                        item.Media.FilePath = item.TargetPath;
                    }

                    item.Status = "Hotovo";
                    item.Media.Status = "Hotovo";
                    await catalog.RecordOperationAsync(runId, item, sourceHash, "Hotovo", null, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    item.Status = "Chyba";
                    if (temporaryPath is not null && File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }

                    await catalog.RecordOperationAsync(runId, item, null, "Chyba", exception.Message, cancellationToken);
                }
                finally
                {
                    if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }

                completed++;
                progress?.Report((completed, executablePlan.Count));
            }

            await catalog.CompleteRunAsync(runId, errors == 0 ? "Hotovo" : "Dokončeno s chybami", cancellationToken);
            return runId;
        }
        catch (OperationCanceledException)
        {
            await catalog.CompleteRunAsync(runId, "Přerušeno", CancellationToken.None);
            throw;
        }
    }

    public async Task<long> UndoAsync(
        long originalRunId,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        await catalog.InitializeAsync(cancellationToken);
        var operations = await catalog.GetCompletedOperationsAsync(originalRunId, cancellationToken);
        if (operations.Count == 0)
        {
            throw new InvalidOperationException("Tento běh neobsahuje žádné dokončené operace k vrácení.");
        }

        var undoRunId = await catalog.StartRunAsync($"Vrácení #{originalRunId}", cancellationToken);
        var completed = 0;
        var errors = 0;

        try
        {
            foreach (var operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? temporaryPath = null;
                try
                {
                    if (!File.Exists(operation.TargetPath))
                        throw new FileNotFoundException("Cílový soubor již neexistuje.", operation.TargetPath);

                    var currentHash = await HashService.ComputeSha256Async(operation.TargetPath, cancellationToken);
                    if (!currentHash.Equals(operation.SourceHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Soubor byl po běhu změněn; vrácení bylo zablokováno.");

                    if (operation.Action == PlannedAction.Copy)
                    {
                        File.Delete(operation.TargetPath);
                    }
                    else if (operation.Action is PlannedAction.Move or PlannedAction.Quarantine)
                    {
                        if (File.Exists(operation.SourcePath))
                            throw new IOException("Původní cesta je již obsazená; vrácení bylo zablokováno.");

                        Directory.CreateDirectory(Path.GetDirectoryName(operation.SourcePath)!);
                        temporaryPath = operation.SourcePath + $".fotoarchiv-{Guid.NewGuid():N}.partial";
                        await CopyAsync(operation.TargetPath, temporaryPath, cancellationToken);
                        var restoredHash = await HashService.ComputeSha256Async(temporaryPath, cancellationToken);
                        if (!restoredHash.Equals(operation.SourceHash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Kontrolní součet obnoveného souboru nesouhlasí.");

                        File.Move(temporaryPath, operation.SourcePath, false);
                        temporaryPath = null;
                        File.Delete(operation.TargetPath);
                    }

                    await catalog.RecordRawOperationAsync(undoRunId, operation.TargetPath, operation.SourcePath,
                        "Undo", operation.SourceHash, "Hotovo", null, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    await catalog.RecordRawOperationAsync(undoRunId, operation.TargetPath, operation.SourcePath,
                        "Undo", operation.SourceHash, "Chyba", exception.Message, cancellationToken);
                }
                finally
                {
                    if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }

                completed++;
                progress?.Report((completed, operations.Count));
            }

            await catalog.CompleteRunAsync(undoRunId, errors == 0 ? "Hotovo" : "Dokončeno s chybami", cancellationToken);
            return undoRunId;
        }
        catch (OperationCanceledException)
        {
            await catalog.CompleteRunAsync(undoRunId, "Přerušeno", CancellationToken.None);
            throw;
        }
    }

    private static async Task CopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
