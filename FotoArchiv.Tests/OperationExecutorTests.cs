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
    public async Task Move_RollsBackVerifiedTargetWhenSourceCannotBeDeleted()
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

            await executor.ExecuteAsync([plan], "Test failed move", null, CancellationToken.None);

            Assert.Equal("Chyba", plan.Status);
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(target));
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
        }
    }
}
