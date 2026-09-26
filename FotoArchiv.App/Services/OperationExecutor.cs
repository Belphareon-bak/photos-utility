using FotoArchiv.App.Models;

namespace FotoArchiv.App.Services;

public sealed class OperationExecutor(CatalogService catalog)
{
    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        using var operationLock = catalog.AcquireExclusiveOperationLock();
        await RecoverPendingCoreAsync(cancellationToken);
    }

    private async Task RecoverPendingCoreAsync(CancellationToken cancellationToken)
    {
        await catalog.InitializeAsync(cancellationToken);
        var unresolved = new List<string>();
        foreach (var operation in await catalog.GetPendingOperationsAsync(cancellationToken))
        {
            var sourceExists = File.Exists(operation.SourcePath);
            var targetExists = File.Exists(operation.TargetPath);
            var sourceMatches = sourceExists &&
                (await HashService.ComputeSha256Async(operation.SourcePath, cancellationToken))
                .Equals(operation.SourceHash, StringComparison.OrdinalIgnoreCase);
            var targetMatches = targetExists &&
                (await HashService.ComputeSha256Async(operation.TargetPath, cancellationToken))
                .Equals(operation.SourceHash, StringComparison.OrdinalIgnoreCase);

            var completed = operation.Action switch
            {
                "Copy" => targetMatches,
                "Move" or "Quarantine" or "UndoMove" => targetMatches && !sourceExists,
                "UndoCopy" => !sourceExists && targetMatches,
                _ => false
            };
            var notApplied = operation.Action switch
            {
                "Copy" or "Move" or "Quarantine" or "UndoMove" => sourceMatches && !targetExists,
                "UndoCopy" => sourceMatches && targetMatches,
                _ => false
            };

            if (completed || notApplied)
                RemoveRecordedTemporaryCopy(operation);

            if (completed)
                await catalog.FinishOperationAsync(operation.Id, "Hotovo", "Dokončení potvrzeno kontrolním součtem po přerušení.", cancellationToken);
            else if (notApplied)
                await catalog.FinishOperationAsync(operation.Id, "Chyba", "Operace se před přerušením neprovedla.", cancellationToken);
            else
                unresolved.Add($"#{operation.Id}: {operation.SourcePath} -> {operation.TargetPath}");
        }

        await catalog.MarkPendingRunsForReviewAsync(cancellationToken);
        await catalog.CloseInterruptedRunsAsync(cancellationToken);
        if (unresolved.Count > 0)
            throw new InvalidOperationException("Rozpracované operace mají nejednoznačný stav. Soubory nebyly změněny; " +
                "před dalším během je nutná ruční kontrola: " + string.Join("; ", unresolved));
    }

    public async Task<long> ExecuteAsync(
        IReadOnlyList<OrganizationPlanItem> plan,
        string runType,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        using var operationLock = catalog.AcquireExclusiveOperationLock();
        await RecoverPendingCoreAsync(cancellationToken);
        var runId = await catalog.StartRunAsync(runType, cancellationToken);
        var completed = 0;
        var errors = 0;
        var executablePlan = plan.Where(item => item.Action is not PlannedAction.Skip and not PlannedAction.Error).ToList();

        try
        {
            foreach (var item in plan.Where(item => item.Action == PlannedAction.Error))
            {
                item.Status = "Chyba";
                errors++;
                await catalog.RecordOperationAsync(runId, item, null, "Chyba",
                    item.Warning ?? "Plán nelze provést.", cancellationToken);
            }
            foreach (var item in plan.Where(item => item.Action == PlannedAction.Skip))
            {
                item.Status = "Přeskočeno";
            }

            foreach (var item in executablePlan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? temporaryPath = null;
                long? operationId = null;

                try
                {
                    if (!File.Exists(item.SourcePath))
                        throw new FileNotFoundException("Zdrojový soubor již není dostupný.", item.SourcePath);
                    if (File.Exists(item.TargetPath))
                        throw new IOException("Cílový soubor již existuje; nebyl přepsán.");

                    var sourceHash = await HashService.ComputeSha256Async(item.SourcePath, cancellationToken);
                    temporaryPath = item.TargetPath + $".fotoarchiv-{Guid.NewGuid():N}.partial";
                    operationId = await catalog.BeginOperationAsync(runId, item.SourcePath, item.TargetPath,
                        item.Action.ToString(), sourceHash, null, temporaryPath, cancellationToken);
                    Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                    await CopyAsync(item.SourcePath, temporaryPath, cancellationToken);

                    var targetHash = await HashService.ComputeSha256Async(temporaryPath, cancellationToken);
                    if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Kontrolní součet kopie nesouhlasí se zdrojem.");

                    File.Move(temporaryPath, item.TargetPath, false);
                    temporaryPath = null;
                    VerifyTimestamps(item.SourcePath, item.TargetPath);
                    var publishedHash = await HashService.ComputeSha256Async(item.TargetPath, cancellationToken);
                    if (!publishedHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Zveřejněný cílový soubor se po přesunu změnil; zdroj zůstává zachován.");

                    if (item.Action is PlannedAction.Move or PlannedAction.Quarantine)
                    {
                        File.Delete(item.SourcePath);
                        item.Media.FilePath = item.TargetPath;
                    }

                    await catalog.FinishOperationAsync(operationId.Value, "Hotovo", null, cancellationToken);
                    operationId = null;
                    item.Status = "Hotovo";
                    item.Media.Status = "Hotovo";
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    item.Status = "Chyba";
                    if (temporaryPath is not null && File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }

                    if (operationId is long pendingId)
                    {
                        if (!File.Exists(item.TargetPath) && File.Exists(item.SourcePath))
                            await catalog.FinishOperationAsync(pendingId, "Chyba", exception.Message, cancellationToken);
                        else
                            throw new InvalidOperationException(
                                $"Operace má nejednoznačný stav; ověřte zdroj i cíl: {item.SourcePath} -> {item.TargetPath}", exception);
                    }
                    else
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

    public async Task<(long RunId, int ErrorCount)> UndoAsync(
        long originalRunId,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        using var operationLock = catalog.AcquireExclusiveOperationLock();
        await RecoverPendingCoreAsync(cancellationToken);
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
                long? operationId = null;
                try
                {
                    if (!File.Exists(operation.TargetPath))
                        throw new FileNotFoundException("Cílový soubor již neexistuje.", operation.TargetPath);

                    var currentHash = await HashService.ComputeSha256Async(operation.TargetPath, cancellationToken);
                    if (!currentHash.Equals(operation.SourceHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Soubor byl po běhu změněn; vrácení bylo zablokováno.");

                    if (operation.Action == PlannedAction.Copy)
                    {
                        if (!File.Exists(operation.SourcePath))
                            throw new IOException("Původní soubor chybí; ověřená kopie zůstává zachována.");
                        var originalHash = await HashService.ComputeSha256Async(operation.SourcePath, cancellationToken);
                        if (!originalHash.Equals(operation.SourceHash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Původní soubor se změnil; ověřená kopie zůstává zachována.");
                        operationId = await catalog.BeginOperationAsync(undoRunId, operation.TargetPath,
                            operation.SourcePath, "UndoCopy", operation.SourceHash, operation.Id, null, cancellationToken);
                        File.Delete(operation.TargetPath);
                    }
                    else if (operation.Action is PlannedAction.Move or PlannedAction.Quarantine)
                    {
                        if (File.Exists(operation.SourcePath))
                            throw new IOException("Původní cesta je již obsazená; vrácení bylo zablokováno.");

                        temporaryPath = operation.SourcePath + $".fotoarchiv-{Guid.NewGuid():N}.partial";
                        operationId = await catalog.BeginOperationAsync(undoRunId, operation.TargetPath,
                            operation.SourcePath, "UndoMove", operation.SourceHash, operation.Id, temporaryPath, cancellationToken);
                        Directory.CreateDirectory(Path.GetDirectoryName(operation.SourcePath)!);
                        await CopyAsync(operation.TargetPath, temporaryPath, cancellationToken);
                        var restoredHash = await HashService.ComputeSha256Async(temporaryPath, cancellationToken);
                        if (!restoredHash.Equals(operation.SourceHash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Kontrolní součet obnoveného souboru nesouhlasí.");

                        File.Move(temporaryPath, operation.SourcePath, false);
                        temporaryPath = null;
                        VerifyTimestamps(operation.TargetPath, operation.SourcePath);
                        var publishedHash = await HashService.ComputeSha256Async(operation.SourcePath, cancellationToken);
                        if (!publishedHash.Equals(operation.SourceHash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Obnovený soubor se po zveřejnění změnil; archivovaná kopie zůstává zachována.");
                        File.Delete(operation.TargetPath);
                    }

                    await catalog.FinishOperationAsync(operationId!.Value, "Hotovo", null, cancellationToken);
                    operationId = null;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    if (operationId is long pendingId)
                    {
                        var notApplied = operation.Action == PlannedAction.Copy
                            ? File.Exists(operation.TargetPath) && File.Exists(operation.SourcePath)
                            : File.Exists(operation.TargetPath) && !File.Exists(operation.SourcePath);
                        if (notApplied)
                            await catalog.FinishOperationAsync(pendingId, "Chyba", exception.Message, cancellationToken);
                        else
                            throw new InvalidOperationException(
                                $"Vrácení má nejednoznačný stav: {operation.TargetPath} -> {operation.SourcePath}", exception);
                    }
                    else
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
            return (undoRunId, errors);
        }
        catch (OperationCanceledException)
        {
            await catalog.CompleteRunAsync(undoRunId, "Přerušeno", CancellationToken.None);
            throw;
        }
    }

    private static async Task CopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        PreserveTimestamps(source, target);
    }

    private static void RemoveRecordedTemporaryCopy(PendingOperation operation)
    {
        if (operation.TemporaryPath is null) return;
        var expected = operation.TargetPath + ".fotoarchiv-";
        var path = operation.TemporaryPath;
        var suffixLength = ".partial".Length;
        if (!path.StartsWith(expected, StringComparison.Ordinal) ||
            !path.EndsWith(".partial", StringComparison.Ordinal) ||
            !Guid.TryParseExact(path.Substring(expected.Length, path.Length - expected.Length - suffixLength), "N", out _))
            throw new InvalidOperationException($"Rozpracovaná operace #{operation.Id} má neplatnou cestu dočasné kopie.");
        if (File.Exists(path)) File.Delete(path);
    }

    // Cas souboru muze byt jedinym datem snimku bez EXIF. Selhani zachovani
    // razitek zabrani smazani zdroje; na Windows se kontroluji obe hodnoty.
    private static void PreserveTimestamps(string source, string target)
    {
        var sourceInfo = new FileInfo(source);
        File.SetLastWriteTimeUtc(target, sourceInfo.LastWriteTimeUtc);
        if (OperatingSystem.IsWindows())
            File.SetCreationTimeUtc(target, sourceInfo.CreationTimeUtc);
        VerifyTimestamps(source, target);
    }

    private static void VerifyTimestamps(string source, string target)
    {
        var sourceInfo = new FileInfo(source);
        if (Math.Abs((File.GetLastWriteTimeUtc(target) - sourceInfo.LastWriteTimeUtc).TotalSeconds) > 2)
            throw new IOException("Cílový svazek nezachoval čas poslední změny; zdroj zůstává zachován.");
        if (OperatingSystem.IsWindows() &&
            Math.Abs((File.GetCreationTimeUtc(target) - sourceInfo.CreationTimeUtc).TotalSeconds) > 2)
            throw new IOException("Cílový svazek nezachoval čas vytvoření; zdroj zůstává zachován.");
    }
}
