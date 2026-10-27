// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Git;

public class GitClientTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(null, "HEAD", false)]
    [InlineData("feature/templates", "refs/heads/feature/templates", false)]
    [InlineData("v1", "refs/tags/v1", false)]
    [InlineData("v1", "refs/tags/v1", true)]
    [InlineData("refs/heads/feature/templates", "refs/heads/feature/templates", false)]
    [InlineData("refs/tags/v1", "refs/tags/v1", true)]
    public async Task ResolveReferenceAsync_ResolvesExactRefsAndPeelsAnnotatedTags(string? reference, string advertisedRef, bool annotated)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commitSha = TestGitCommandRunner.DefaultCommitSha;
        var tagObjectSha = new string('a', 40);
        var commands = new TestGitCommandRunner
        {
            Output = annotated
                ? $"{tagObjectSha}\t{advertisedRef}\n{commitSha}\t{advertisedRef}^{{}}\n"
                : $"{commitSha}\t{advertisedRef}\n"
        };
        var client = new GitClient(commands);

        var resolved = await client.ResolveReferenceAsync("-repository with spaces", reference, workspace.WorkspaceRoot, GitCommandMode.Captured, TestContext.Current.CancellationToken);

        Assert.Equal(commitSha, resolved);
        var command = Assert.Single(commands.Commands);
        var requestedRef = reference ?? "HEAD";
        string[] candidates = requestedRef == "HEAD" || requestedRef.StartsWith("refs/", StringComparison.Ordinal)
            ? [$"{requestedRef}^{{}}", requestedRef]
            : [$"refs/heads/{requestedRef}", $"refs/tags/{requestedRef}^{{}}", $"refs/tags/{requestedRef}"];
        Assert.Equal(["ls-remote", "--", "-repository with spaces", .. candidates], command.Arguments);
        Assert.Equal(workspace.Path, command.WorkingDirectory.FullName);
        Assert.Equal(GitCommandMode.Captured, command.Mode);
    }

    [Fact]
    public async Task ResolveReferenceAsync_BranchTakesPrecedenceOverTag()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner
        {
            Output = $"{new string('a', 40)}\trefs/tags/same^{{}}\n{TestGitCommandRunner.DefaultCommitSha}\trefs/heads/same\n"
        };
        var client = new GitClient(commands);

        Assert.Equal(TestGitCommandRunner.DefaultCommitSha,
            await client.ResolveReferenceAsync("repository", "same", workspace.WorkspaceRoot, GitCommandMode.Captured, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(nameof(GitCommandMode.Captured), 40)]
    [InlineData(nameof(GitCommandMode.Interactive), 64)]
    [InlineData(nameof(GitCommandMode.Unattended), 64)]
    public async Task ResolveReferenceAsync_PreservesInteractionModeAndObjectIdLength(string mode, int length)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commit = new string('a', length);
        var commands = new TestGitCommandRunner { Output = $"{commit}\trefs/heads/main\n" };
        var client = new GitClient(commands);
        var interactionMode = Enum.Parse<GitCommandMode>(mode);

        var resolved = await client.ResolveReferenceAsync("repository", "refs/heads/main", workspace.WorkspaceRoot,
            interactionMode, TestContext.Current.CancellationToken);

        Assert.Equal(commit, resolved);
        Assert.Equal(interactionMode, Assert.Single(commands.Commands).Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha\trefs/heads/missing")]
    [InlineData("0123456789abcdef0123456789abcdef01234567\trefs/heads/different")]
    public async Task ResolveReferenceAsync_MissingOrInvalidRefIsExplicitFailure(string output)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner { Output = output };
        var client = new GitClient(commands);

        var exception = await Assert.ThrowsAsync<GitReferenceException>(() =>
            client.ResolveReferenceAsync("repository", "missing", workspace.WorkspaceRoot, GitCommandMode.Captured, TestContext.Current.CancellationToken));

        Assert.Equal("missing", exception.Reference);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task ResolveReferenceAsync_FullShaIsAlreadyResolved(int length)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner();
        var client = new GitClient(commands);

        var resolved = await client.ResolveReferenceAsync("repository", new string('A', length), workspace.WorkspaceRoot, GitCommandMode.Captured, TestContext.Current.CancellationToken);

        Assert.Equal(new string('a', length), resolved);
        Assert.Empty(commands.Commands);
    }

    [Fact]
    public async Task BareRepository_FetchAndQueriesExplicitlyIdentifyRepository()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var commands = new TestGitCommandRunner();
        var client = new GitClient(commands);
        var repository = new GitRepositoryLocation(new DirectoryInfo(Path.Combine(workspace.Path, "repository.git")), IsBare: true);
        var token = TestContext.Current.CancellationToken;

        await client.InitializeAsync(workspace.WorkspaceRoot, repository, objectFormat: null, GitCommandMode.Interactive, token);
        await client.FetchAsync(repository, "repository with spaces", "refs/heads/main", GitCommandMode.Unattended, token);
        Assert.Equal(TestGitCommandRunner.DefaultCommitSha, await client.GetCommitAsync(repository, "FETCH_HEAD^{commit}", GitCommandMode.Interactive, token));

        Assert.Collection(commands.Commands,
            command =>
            {
                Assert.Equal(["init", "--bare", "--", repository.Directory.FullName], command.Arguments);
                Assert.Equal(workspace.Path, command.WorkingDirectory.FullName);
                Assert.Equal(GitCommandMode.Interactive, command.Mode);
            },
            command =>
            {
                Assert.Equal([$"--git-dir={repository.Directory.FullName}", "fetch", "--depth", "1", "--no-tags", "--", "repository with spaces", "refs/heads/main"], command.Arguments);
                Assert.Equal(repository.Directory.FullName, command.WorkingDirectory.FullName);
                Assert.Equal(GitCommandMode.Unattended, command.Mode);
            },
            command =>
            {
                Assert.Equal([$"--git-dir={repository.Directory.FullName}", "rev-parse", "FETCH_HEAD^{commit}"], command.Arguments);
                Assert.Equal(GitCommandMode.Interactive, command.Mode);
            });
    }

    [Theory]
    [InlineData(GitTreeEntry.RegularFileMode, "blob")]
    [InlineData(GitTreeEntry.ExecutableFileMode, "blob")]
    [InlineData(GitTreeEntry.SymbolicLinkMode, "blob")]
    [InlineData(GitTreeEntry.DirectoryMode, "tree")]
    public async Task GetFile_ReturnsEntryMetadataWithoutApplyingContentPolicy(string mode, string type)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string path = "settings[1] with spaces.json";
        var objectId = TestGitCommandRunner.DefaultCommitSha;
        var commands = new TestGitCommandRunner { Output = $"{mode} {type} {objectId}\t{path}\0" };
        var client = new GitClient(commands);
        var repository = new GitRepositoryLocation(workspace.WorkspaceRoot, IsBare: true);

        var entry = await client.GetFileAsync(repository, objectId, path, GitCommandMode.Interactive, TestContext.Current.CancellationToken);

        Assert.Equal(new GitTreeEntry(mode, type, objectId), entry);
        var command = Assert.Single(commands.Commands);
        Assert.Equal([$"--git-dir={workspace.Path}", "ls-tree", "-z", objectId, "--", path], command.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("100644 blob object\tfile")]
    [InlineData("malformed\tfile\0")]
    [InlineData("100644 blob object\tfirst\0" + "100644 blob object\tsecond\0")]
    public async Task GetFile_RequiresOneCompleteTreeEntry(string output)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var client = new GitClient(new TestGitCommandRunner { Output = output });
        var repository = new GitRepositoryLocation(workspace.WorkspaceRoot, IsBare: true);

        Assert.Null(await client.GetFileAsync(repository, TestGitCommandRunner.DefaultCommitSha, "file", GitCommandMode.Interactive, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BlobQueries_ReportSizeAndPreserveContent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string content = "{\r\n\"custom\": \"value\"\r\n}";
        var objectId = TestGitCommandRunner.DefaultCommitSha;
        var commands = new TestGitCommandRunner
        {
            RunCallback = (command, _) => Task.FromResult(command.Arguments[2] == "-s" ? "23\n" : content)
        };
        var client = new GitClient(commands);
        var repository = new GitRepositoryLocation(workspace.WorkspaceRoot, IsBare: true);

        Assert.Equal(23, await client.GetObjectSizeAsync(repository, objectId, GitCommandMode.Interactive, TestContext.Current.CancellationToken));
        Assert.Equal(content, await client.ReadBlobAsync(repository, objectId, GitCommandMode.Interactive, TestContext.Current.CancellationToken));
        Assert.Collection(commands.Commands,
            command => Assert.Equal([$"--git-dir={workspace.Path}", "cat-file", "-s", objectId], command.Arguments),
            command => Assert.Equal([$"--git-dir={workspace.Path}", "cat-file", "blob", objectId], command.Arguments));
    }
}
