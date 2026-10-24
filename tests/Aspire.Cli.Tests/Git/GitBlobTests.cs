// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Git;

public class GitBlobTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("sha1", "settings[1].json")]
    [InlineData("sha256", "settings with spaces.json")]
    [InlineData("sha1", "\u65e5\u672c\u8a9e.json")]
    public async Task FetchAndReadBlob_PreservesExactBytesWithoutCheckout(string objectFormat, string path)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        byte[] content = [0, 1, 10, 13, 10, 0xc3, 0xa9, 0xff, 42];
        var (repository, commit) = await CreateRepositoryAsync(workspace, path, content, objectFormat);
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var runner = provider.GetRequiredService<IGitRunner>();
        var destination = new DirectoryInfo(Path.Combine(workspace.Path, "bare repository"));

        await runner.FetchAsync(Path.GetRelativePath(workspace.WorkspaceRoot.FullName, repository.FullName), destination, commit,
            TestContext.Current.CancellationToken);
        var blob = await runner.GetBlobIdAsync(destination, commit, path, TestContext.Current.CancellationToken);
        var actual = await runner.ReadBlobAsync(destination, blob, content.Length, TestContext.Current.CancellationToken);

        Assert.Equal(content, actual);
        Assert.Equal(commit.Length, blob.Length);
        Assert.True(File.Exists(Path.Combine(destination.FullName, "HEAD")));
        Assert.True(Directory.Exists(Path.Combine(destination.FullName, "objects")));
        Assert.False(File.Exists(Path.Combine(destination.FullName, path)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("line one\r\nline two\nno final newline")]
    [InlineData("{\"message\":\"\u65e5\u672c\u8a9e\"}\r\n")]
    public async Task ReadBlob_PreservesTextAndEmptyFiles(string text)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var content = System.Text.Encoding.UTF8.GetBytes(text);
        var (repository, commit) = await CreateRepositoryAsync(workspace, "content", content);
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var runner = provider.GetRequiredService<IGitRunner>();
        var gitDirectory = new DirectoryInfo(Path.Combine(repository.FullName, ".git"));
        var blob = await runner.GetBlobIdAsync(gitDirectory, commit, "content", TestContext.Current.CancellationToken);

        var actual = await runner.ReadBlobAsync(gitDirectory, blob, content.Length, TestContext.Current.CancellationToken);

        Assert.Equal(content, actual);
    }

    [Fact]
    public async Task Fetch_ReadsPinnedCommitOutsideDefaultBranchTip()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        byte[] content = [1, 2, 3];
        var (repository, commit) = await CreateRepositoryAsync(workspace, "content", content);
        await File.WriteAllBytesAsync(Path.Combine(repository.FullName, "content"), [4, 5, 6]);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-am", "Move branch");
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var runner = provider.GetRequiredService<IGitRunner>();
        var destination = new DirectoryInfo(Path.Combine(workspace.Path, "bare"));

        await runner.FetchAsync(repository.FullName, destination, commit, TestContext.Current.CancellationToken);
        var blob = await runner.GetBlobIdAsync(destination, commit, "content", TestContext.Current.CancellationToken);

        Assert.Equal(content, await runner.ReadBlobAsync(destination, blob, content.Length, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveReference_FailureDoesNotLogPrivateRepositoryOrDiagnostics()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var logger = new FakeLogger<ProcessExecutionFactory>();
        var services = CreateServices(workspace);
        services.AddSingleton<IProcessExecutionFactory>(new ProcessExecutionFactory(new HostEnvironment(), logger));
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() => provider.GetRequiredService<IGitRunner>().ResolveReferenceAsync(
            Path.Combine(workspace.Path, "private-repository-secret-marker"), null, TestContext.Current.CancellationToken));

        Assert.Equal(GitFailure.RepositoryUnavailable, exception.Failure);
        Assert.Equal(Resources.ErrorStrings.GitRepositoryUnavailable, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public async Task Fetch_IsolatesRepositoryEnvironmentAndSupportsExplicitBareDiscovery()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (repository, commit) = await CreateRepositoryAsync(workspace, "content", [1, 2, 3]);
        var actualFactory = new ProcessExecutionFactory(new HostEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        var processes = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (file, arguments, environment, directory, options) =>
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo(file, arguments) { WorkingDirectory = directory.FullName };
                startInfo.Environment["GIT_DIR"] = Path.Combine(repository.FullName, ".git");
                startInfo.Environment["GIT_WORK_TREE"] = repository.FullName;
                startInfo.Environment["GIT_INDEX_FILE"] = Path.Combine(repository.FullName, ".git", "index");
                startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
                startInfo.Environment["GIT_CONFIG_KEY_0"] = "safe.bareRepository";
                startInfo.Environment["GIT_CONFIG_VALUE_0"] = "explicit";
                foreach (var variable in environment!)
                {
                    startInfo.Environment[variable.Key] = variable.Value;
                }
                var execution = actualFactory.CreateExecution(startInfo, options);
                Assert.False(execution.EnvironmentVariables.ContainsKey("GIT_DIR"));
                Assert.False(execution.EnvironmentVariables.ContainsKey("GIT_WORK_TREE"));
                Assert.False(execution.EnvironmentVariables.ContainsKey("GIT_INDEX_FILE"));
                Assert.Equal("explicit", execution.EnvironmentVariables["GIT_CONFIG_VALUE_0"]);
                return execution;
            }
        };
        var services = CreateServices(workspace);
        services.AddSingleton<IProcessExecutionFactory>(processes);
        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IGitRunner>();
        var destination = new DirectoryInfo(Path.Combine(workspace.Path, "bare"));

        await runner.FetchAsync(repository.FullName, destination, commit, TestContext.Current.CancellationToken);
        var blob = await runner.GetBlobIdAsync(destination, commit, "content", TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2, 3 }, await runner.ReadBlobAsync(destination, blob, 3, TestContext.Current.CancellationToken));
        Assert.Equal(commit, (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "main"))).Trim());
    }

    [Theory]
    [InlineData("missing", nameof(GitFailure.FileNotFound))]
    [InlineData("directory", nameof(GitFailure.FileNotRegular))]
    [InlineData("link", nameof(GitFailure.FileNotRegular))]
    public async Task GetBlobId_RejectsMissingFilesDirectoriesAndSymlinks(string path, string failure)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (repository, commit) = await CreateRepositoryAsync(workspace, "directory/content", [1, 2, 3]);
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var runner = provider.GetRequiredService<IGitRunner>();
        var gitDirectory = new DirectoryInfo(Path.Combine(repository.FullName, ".git"));
        var blob = await runner.GetBlobIdAsync(gitDirectory, commit, "directory/content", TestContext.Current.CancellationToken);
        // A symlink-mode index entry avoids requiring Windows symlink privileges.
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "update-index", "--add", "--cacheinfo", $"120000,{blob},link");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-m", "Add symlink entry");
        commit = (await File.ReadAllTextAsync(Path.Combine(gitDirectory.FullName, "refs", "heads", "main"))).Trim();

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            runner.GetBlobIdAsync(gitDirectory, commit, path, TestContext.Current.CancellationToken));

        Assert.Equal(Enum.Parse<GitFailure>(failure), exception.Failure);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReadBlob_EnforcesExactSizeLimitBeforeReadingContent(int delta)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var content = new byte[1024];
        var (repository, commit) = await CreateRepositoryAsync(workspace, "content", content);
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var runner = provider.GetRequiredService<IGitRunner>();
        var gitDirectory = new DirectoryInfo(Path.Combine(repository.FullName, ".git"));
        var blob = await runner.GetBlobIdAsync(gitDirectory, commit, "content", TestContext.Current.CancellationToken);

        if (delta < 0)
        {
            var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
                runner.ReadBlobAsync(gitDirectory, blob, content.Length + delta, TestContext.Current.CancellationToken));
            Assert.Equal(GitFailure.BlobTooLarge, exception.Failure);
        }
        else
        {
            Assert.Equal(content, await runner.ReadBlobAsync(gitDirectory, blob, content.Length + delta, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("../outside")]
    [InlineData("directory/../content")]
    [InlineData("directory\\content")]
    [InlineData("directory//content")]
    [InlineData("content\n")]
    public async Task GetBlobId_RejectsInvalidPathsBeforeStartingGit(string path)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var processes = new TestProcessExecutionFactory();
        var services = CreateServices(workspace);
        services.AddSingleton<IProcessExecutionFactory>(processes);
        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRequiredService<IGitRunner>().GetBlobIdAsync(
            workspace.WorkspaceRoot, TestGitRunner.DefaultCommitSha, path, TestContext.Current.CancellationToken));

        Assert.Empty(processes.CreatedExecutions);
    }

    private IServiceCollection CreateServices(TemporaryWorkspace workspace)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IProcessExecutionFactory>(
            new ProcessExecutionFactory(new HostEnvironment(), NullLogger<ProcessExecutionFactory>.Instance));
        return services;
    }

    private async Task<(DirectoryInfo Repository, string Commit)> CreateRepositoryAsync(
        TemporaryWorkspace workspace, string path, byte[] content, string objectFormat = "sha1")
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var repository = workspace.CreateDirectory("source");
        var file = Path.Combine(repository.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, content);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "init", "--initial-branch=main", $"--object-format={objectFormat}");
        await GitTestHelper.ConfigureGitIdentityAsync(repository.FullName, outputHelper);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "config", "core.autocrlf", "false");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "add", ".");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-m", "Add content");
        var commit = (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "main"))).Trim();
        return (repository, commit);
    }
}
