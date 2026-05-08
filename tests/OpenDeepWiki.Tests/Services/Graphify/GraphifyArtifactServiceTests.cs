using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Graphify;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Graphify;

public class GraphifyArtifactServiceTests
{
    [Fact]
    public async Task EnqueueGenerationAsync_ForCompletedRepository_CreatesPendingArtifact()
    {
        await using var context = CreateContext();
        var seed = SeedRepository(context, RepositoryStatus.Completed);
        var service = new GraphifyArtifactService(context);

        var result = await service.EnqueueGenerationAsync(seed.RepositoryId, seed.BranchId);

        Assert.True(result.Success);
        var artifact = await context.GraphifyArtifacts.SingleAsync();
        Assert.Equal(seed.RepositoryId, artifact.RepositoryId);
        Assert.Equal(seed.BranchId, artifact.RepositoryBranchId);
        Assert.Equal(GraphifyArtifactStatus.Pending, artifact.Status);
        Assert.Null(artifact.CommitId);
        Assert.Null(artifact.EntryFilePath);
    }

    [Fact]
    public async Task EnqueueGenerationAsync_ForProcessingRepository_DoesNotCreateArtifact()
    {
        await using var context = CreateContext();
        var seed = SeedRepository(context, RepositoryStatus.Processing);
        var service = new GraphifyArtifactService(context);

        var result = await service.EnqueueGenerationAsync(seed.RepositoryId, seed.BranchId);

        Assert.False(result.Success);
        Assert.Empty(context.GraphifyArtifacts);
    }

    [Fact]
    public async Task EnqueueGenerationAsync_ForExistingCompletedArtifact_ResetsToPending()
    {
        await using var context = CreateContext();
        var seed = SeedRepository(context, RepositoryStatus.Completed);
        context.GraphifyArtifacts.Add(new GraphifyArtifact
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = seed.RepositoryId,
            RepositoryBranchId = seed.BranchId,
            Status = GraphifyArtifactStatus.Completed,
            CommitId = "old-commit",
            EntryFilePath = "/tmp/old.html",
            GraphJsonPath = "/tmp/old.json",
            ReportPath = "/tmp/old.md",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            ErrorMessage = "old error"
        });
        await context.SaveChangesAsync();
        var service = new GraphifyArtifactService(context);

        var result = await service.EnqueueGenerationAsync(seed.RepositoryId, seed.BranchId);

        Assert.True(result.Success);
        var artifact = await context.GraphifyArtifacts.SingleAsync();
        Assert.Equal(GraphifyArtifactStatus.Pending, artifact.Status);
        Assert.Null(artifact.CommitId);
        Assert.Null(artifact.EntryFilePath);
        Assert.Null(artifact.GraphJsonPath);
        Assert.Null(artifact.ReportPath);
        Assert.Null(artifact.StartedAt);
        Assert.Null(artifact.CompletedAt);
        Assert.Null(artifact.ErrorMessage);
    }

    [Fact]
    public async Task GetCompletedArtifactAsync_ReturnsCompletedArtifactForBranchName()
    {
        await using var context = CreateContext();
        var seed = SeedRepository(context, RepositoryStatus.Completed);
        var expected = new GraphifyArtifact
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = seed.RepositoryId,
            RepositoryBranchId = seed.BranchId,
            Status = GraphifyArtifactStatus.Completed,
            CommitId = "abc123",
            EntryFilePath = "/tmp/graph.html"
        };
        context.GraphifyArtifacts.Add(expected);
        await context.SaveChangesAsync();
        var service = new GraphifyArtifactService(context);

        var artifact = await service.GetCompletedArtifactAsync("owner", "repo", "main");

        Assert.NotNull(artifact);
        Assert.Equal(expected.Id, artifact.Id);
        Assert.Equal("abc123", artifact.CommitId);
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static SeededRepository SeedRepository(TestDbContext context, RepositoryStatus status)
    {
        var repositoryId = Guid.NewGuid().ToString();
        var branchId = Guid.NewGuid().ToString();

        context.Repositories.Add(new Repository
        {
            Id = repositoryId,
            OrgName = "owner",
            RepoName = "repo",
            GitUrl = "https://github.com/owner/repo.git",
            OwnerUserId = Guid.NewGuid().ToString(),
            Status = status
        });

        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repositoryId,
            BranchName = "main"
        });

        context.SaveChanges();
        return new SeededRepository(repositoryId, branchId);
    }

    private sealed record SeededRepository(string RepositoryId, string BranchId);

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : OpenDeepWiki.EFCore.MasterDbContext(options);
}
