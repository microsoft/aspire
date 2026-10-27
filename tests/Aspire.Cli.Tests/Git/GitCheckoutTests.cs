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

public class GitCheckoutTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("sha1", "settings[1].json")]
    [InlineData("sha256", "settings with spaces.json")]
    [InlineData("sha1", "\u65e5\u672c\u8a9e.json")]
    public async Task Clone_ChecksOutExactCommit(string objectFormat, string path)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        byte[] content = [0, 1, 10, 13, 10, 0xc3, 0xa9, 0xff, 42];
        var (repository, commit) = await CreateRepositoryAsync(workspace, path, content, objectFormat);
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var runner = provider.GetRequiredService<IGitRunner>();
        var destination = new DirectoryInfo(Path.Combine(workspace.Path, "checkout"));

        await runner.CloneAsync(Path.GetRelativePath(workspace.WorkspaceRoot.FullName, repository.FullName), destination, commit,
            TestContext.Current.CancellationToken);
        var actual = await File.ReadAllBytesAsync(Path.Combine(destination.FullName, path));

        Assert.Equal(content, actual);
        Assert.Equal(commit, (await File.ReadAllTextAsync(Path.Combine(destination.FullName, ".git", "HEAD"))).Trim());
    }

    [Fact]
    public async Task Clone_ChecksOutPinnedCommitOutsideDefaultBranchTip()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        byte[] content = [1, 2, 3];
        var (repository, commit) = await CreateRepositoryAsync(workspace, "content", content);
        await File.WriteAllBytesAsync(Path.Combine(repository.FullName, "content"), [4, 5, 6]);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-am", "Move branch");
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var runner = provider.GetRequiredService<IGitRunner>();
        var destination = new DirectoryInfo(Path.Combine(workspace.Path, "checkout"));

        await runner.CloneAsync(repository.FullName, destination, commit, TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(destination.FullName, "content")));
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
    public async Task Clone_IsolatesRepositoryEnvironment()
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
        var destination = new DirectoryInfo(Path.Combine(workspace.Path, "checkout"));

        await runner.CloneAsync(repository.FullName, destination, commit, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(destination.FullName, "content")));
        Assert.Equal(commit, (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "main"))).Trim());
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
