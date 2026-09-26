using FotoArchiv.App.Models;
using FotoArchiv.App.Services;

namespace FotoArchiv.Tests;

public sealed class OperationExecutorTests
{
    [Fact]
    public async Task Copy_VerifiesContentAndUndoRemovesOnlyCreatedCopy()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/Okoř/2025-09-12/001.jpg");
        var bytes = Enumerable.Range(0, 32_000).Select(value => (byte)(value % 253)).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        var media = TestMedia.Create(source, directory.Path, fileSize: bytes.Length);
        var plan = new OrganizationPlanItem
        {
            Media = media,
            SourcePath = source,
            TargetPath = target,
            Action = PlannedAction.Copy
        };
        var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));

        var runId = await executor.ExecuteAsync([plan], "Test copy", null, CancellationToken.None);

        Assert.True(File.Exists(source));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));

        await executor.UndoAsync(runId, null, CancellationToken.None);

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Copy_PreservesSourceLastWriteTime()
    {
        // Kopie pres FileStream razitka neprenasi. Bez PreserveTimestamps dostane cil
        // aktualni cas a u souboru bez EXIF se ztrati jediny udaj o dobe porizeni.
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/Okoř/2016-08-13/001.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4, 5]);

        var expected = new DateTime(2016, 8, 13, 22, 3, 52, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, expected);
        var expectedCreation = new DateTime(2015, 3, 2, 12, 0, 0, DateTimeKind.Utc);
        if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(source, expectedCreation);

        var plan = new OrganizationPlanItem
        {
            Media = TestMedia.Create(source, directory.Path, fileSize: 5),
            SourcePath = source,
            TargetPath = target,
            Action = PlannedAction.Copy
        };
        var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));

        await executor.ExecuteAsync([plan], "Test razitek", null, CancellationToken.None);

        Assert.Equal("Hotovo", plan.Status);
        Assert.Equal(expected, File.GetLastWriteTimeUtc(target));
        if (OperatingSystem.IsWindows())
            Assert.InRange(Math.Abs((File.GetCreationTimeUtc(target) - expectedCreation).TotalSeconds), 0, 2);
    }

    [Fact]
    public async Task Move_UsesVerifiedCopyAndUndoRestoresOriginalPath()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.heic");
        var target = directory.File("archive/Okoř/2025-09-12/001.heic");
        var bytes = Enumerable.Range(0, 18_000).Select(value => (byte)(value % 241)).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        var media = TestMedia.Create(source, directory.Path, fileSize: bytes.Length);
        var plan = new OrganizationPlanItem
        {
            Media = media,
            SourcePath = source,
            TargetPath = target,
            Action = PlannedAction.Move
        };
        var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));

        var runId = await executor.ExecuteAsync([plan], "Test move", null, CancellationToken.None);

        Assert.False(File.Exists(source));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));

        await executor.UndoAsync(runId, null, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Move_PreservesBothCopiesForReviewWhenSourceCannotBeDeleted()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/locked.jpg");
        var target = directory.File("archive/Okoř/2025-09-12/001.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4, 5]);
        File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            var media = TestMedia.Create(source, directory.Path, fileSize: 5);
            var plan = new OrganizationPlanItem
            {
                Media = media,
                SourcePath = source,
                TargetPath = target,
                Action = PlannedAction.Move
            };
            var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executor.ExecuteAsync([plan], "Test failed move", null, CancellationToken.None));

            Assert.Equal("Chyba", plan.Status);
            Assert.True(File.Exists(source));
            Assert.True(File.Exists(target));
            Assert.Single(await new CatalogService(directory.File("state/catalog.db"))
                .GetPendingOperationsAsync(CancellationToken.None));
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task PlannedError_IsRecordedAsFailedRun()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/orphan.xmp");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var plan = new OrganizationPlanItem
        {
            Media = TestMedia.Create(source, directory.Path, MediaKind.Sidecar),
            SourcePath = source,
            TargetPath = directory.File("archive/orphan.xmp"),
            Action = PlannedAction.Error,
            Warning = "Cílový sidecar již existuje."
        };
        var catalog = new CatalogService(directory.File("state/catalog.db"));
        var executor = new OperationExecutor(catalog);

        var runId = await executor.ExecuteAsync([plan], "Test preflight error", null, CancellationToken.None);
        var history = await catalog.GetHistoryAsync(CancellationToken.None);
        var run = Assert.Single(history, item => item.RunId == runId);

        Assert.Equal("Chyba", plan.Status);
        Assert.Equal("Dokončeno s chybami", run.Status);
        Assert.Equal(1, run.ErrorCount);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(plan.TargetPath));
    }

    [Fact]
    public async Task Undo_ReportsChangedTargetAsError()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/photo.jpg");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var plan = new OrganizationPlanItem
        {
            Media = TestMedia.Create(source, directory.Path),
            SourcePath = source,
            TargetPath = target,
            Action = PlannedAction.Copy
        };
        var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));
        var runId = await executor.ExecuteAsync([plan], "Test undo error", null, CancellationToken.None);
        await File.WriteAllBytesAsync(target, [7, 8, 9]);

        var outcome = await executor.UndoAsync(runId, null, CancellationToken.None);

        Assert.Equal(1, outcome.ErrorCount);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task UndoCopy_KeepsVerifiedCopyWhenOriginalIsMissing()
    {
        using var directory = new TestDirectory();
        var source = directory.File("source/photo.jpg");
        var target = directory.File("archive/photo.jpg");
        var bytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(source, bytes);
        var plan = new OrganizationPlanItem
        {
            Media = TestMedia.Create(source, directory.Path),
            SourcePath = source,
            TargetPath = target,
            Action = PlannedAction.Copy
        };
        var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));
        var runId = await executor.ExecuteAsync([plan], "Test missing original", null, CancellationToken.None);
        File.Delete(source);

        var outcome = await executor.UndoAsync(runId, null, CancellationToken.None);

        Assert.Equal(1, outcome.ErrorCount);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task ExactDuplicateSidecar_ExecutesThroughQuarantineAndArchive()
    {
        using var directory = new TestDirectory();
        var keptPath = directory.File("source/kept.jpg");
        var droppedPath = directory.File("source/dropped.jpg");
        var sidecarPath = directory.File("source/dropped.xmp");
        await File.WriteAllBytesAsync(keptPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(droppedPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(sidecarPath, [9, 8, 7]);
        var kept = TestMedia.Create(keptPath, Path.GetDirectoryName(keptPath)!);
        var dropped = TestMedia.Create(droppedPath, Path.GetDirectoryName(droppedPath)!);
        var sidecar = TestMedia.Create(sidecarPath, Path.GetDirectoryName(sidecarPath)!, MediaKind.Sidecar);
        kept.DuplicateGroupId = dropped.DuplicateGroupId = "DUP-0001";
        kept.DuplicateConfidence = dropped.DuplicateConfidence = DuplicateConfidence.Exact;
        dropped.WillKeep = false;
        var planner = new OrganizationPlanner();
        var settings = new AppSettings { DestinationRoot = Path.Combine(directory.Path, "archive") };
        var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));

        var quarantine = planner.BuildQuarantinePlan([kept, dropped, sidecar], settings);
        await executor.ExecuteAsync(quarantine, "Duplicity", null, CancellationToken.None);
        Assert.False(File.Exists(droppedPath));
        Assert.True(File.Exists(sidecarPath));

        var archive = planner.Build([kept, dropped, sidecar], settings);
        await executor.ExecuteAsync(archive, "Organizace", null, CancellationToken.None);
        var photoPlan = Assert.Single(archive, item => item.Media == kept);
        var sidecarPlan = Assert.Single(archive, item => item.Media == sidecar);
        Assert.Equal(Path.ChangeExtension(photoPlan.TargetPath, ".xmp"), sidecarPlan.TargetPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(photoPlan.TargetPath));
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(sidecarPlan.TargetPath));
    }
    [Fact]
    public async Task QuarantineRetry_DoesNotMoveCompletedItemAgain()
    {
        using var directory = new TestDirectory();
        var sourceRoot = Path.Combine(directory.Path, "source");
        var keptPath = directory.File("source/kept.jpg");
        var completedPath = directory.File("source/completed.jpg");
        var missingPath = directory.File("source/missing.jpg");
        await File.WriteAllBytesAsync(keptPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(completedPath, [1, 2, 3]);
        var kept = TestMedia.Create(keptPath, sourceRoot);
        var completed = TestMedia.Create(completedPath, sourceRoot);
        var missing = TestMedia.Create(missingPath, sourceRoot);
        foreach (var item in new[] { kept, completed, missing })
        {
            item.DuplicateGroupId = "DUP-0001";
            item.DuplicateConfidence = DuplicateConfidence.Exact;
        }
        completed.WillKeep = false;
        missing.WillKeep = false;
        var planner = new OrganizationPlanner();
        var settings = new AppSettings();
        var executor = new OperationExecutor(new CatalogService(directory.File("state/catalog.db")));

        var firstPlan = planner.BuildQuarantinePlan([kept, completed, missing], settings);
        await executor.ExecuteAsync(firstPlan, "Duplicity", null, CancellationToken.None);
        Assert.Equal("Hotovo", Assert.Single(firstPlan, item => item.Media == completed).Status);
        Assert.Equal("Chyba", Assert.Single(firstPlan, item => item.Media == missing).Status);

        var retry = planner.BuildQuarantinePlan([kept, completed, missing], settings);
        Assert.DoesNotContain(retry, item => item.Media == completed);
        var pending = Assert.Single(retry);
        Assert.Equal(missingPath, pending.SourcePath);
        Assert.Equal(Path.Combine(sourceRoot, "_DuplicatesReview", "1_Exact", "missing.jpg"), pending.TargetPath);
    }

}
