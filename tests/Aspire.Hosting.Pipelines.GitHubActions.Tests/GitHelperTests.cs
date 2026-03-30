// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Pipelines.GitHubActions.Tests;

public class GitHelperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NullLogger _logger = NullLogger.Instance;

    public GitHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aspire-test-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task IsGitRepoAsync_ReturnsFalse_ForNonGitDirectory()
    {
        var result = await GitHelper.IsGitRepoAsync(_tempDir, _logger);

        Assert.False(result);
    }

    [Fact]
    public async Task InitAsync_CreatesGitRepo()
    {
        var result = await GitHelper.InitAsync(_tempDir, _logger);

        Assert.True(result);
        Assert.True(Directory.Exists(Path.Combine(_tempDir, ".git")));
    }

    [Fact]
    public async Task IsGitRepoAsync_ReturnsTrue_AfterInit()
    {
        await GitHelper.InitAsync(_tempDir, _logger);

        var result = await GitHelper.IsGitRepoAsync(_tempDir, _logger);

        Assert.True(result);
    }

    [Fact]
    public async Task GetRepoRootAsync_ReturnsRoot_AfterInit()
    {
        await GitHelper.InitAsync(_tempDir, _logger);

        var root = await GitHelper.GetRepoRootAsync(_tempDir, _logger);

        Assert.NotNull(root);
        // Resolve symlinks for macOS /tmp → /private/tmp
        Assert.Equal(
            Path.GetFullPath(_tempDir),
            Path.GetFullPath(root));
    }

    [Fact]
    public async Task GetRepoRootAsync_ReturnsNull_ForNonGitDirectory()
    {
        var root = await GitHelper.GetRepoRootAsync(_tempDir, _logger);

        Assert.Null(root);
    }

    [Fact]
    public async Task GetRemoteUrlAsync_ReturnsNull_WhenNoRemote()
    {
        await GitHelper.InitAsync(_tempDir, _logger);

        var url = await GitHelper.GetRemoteUrlAsync(_tempDir, _logger);

        Assert.Null(url);
    }

    [Fact]
    public async Task AddRemoteAsync_AddsRemote()
    {
        await GitHelper.InitAsync(_tempDir, _logger);

        var result = await GitHelper.AddRemoteAsync(_tempDir, "https://github.com/test/repo.git", _logger);

        Assert.True(result);

        var url = await GitHelper.GetRemoteUrlAsync(_tempDir, _logger);
        Assert.Equal("https://github.com/test/repo.git", url);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsBranchName_AfterCommit()
    {
        await GitHelper.InitAsync(_tempDir, _logger);

        // Configure git user for commit
        await RunGitAsync(_tempDir, "config user.email test@test.com");
        await RunGitAsync(_tempDir, "config user.name Test");

        // Create a file and commit to establish a branch
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "README.md"), "test");
        await GitHelper.AddAllAndCommitAsync(_tempDir, "Initial commit", _logger);

        var branch = await GitHelper.GetCurrentBranchAsync(_tempDir, _logger);

        Assert.NotNull(branch);
        Assert.NotEmpty(branch);
    }

    [Fact]
    public async Task AddAllAndCommitAsync_CommitsFiles()
    {
        await GitHelper.InitAsync(_tempDir, _logger);
        await RunGitAsync(_tempDir, "config user.email test@test.com");
        await RunGitAsync(_tempDir, "config user.name Test");

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "hello");

        var result = await GitHelper.AddAllAndCommitAsync(_tempDir, "Test commit", _logger);

        Assert.True(result);

        // Verify commit exists via log
        var (exitCode, output) = await RunGitWithOutputAsync(_tempDir, "log --oneline");
        Assert.Equal(0, exitCode);
        Assert.Contains("Test commit", output);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static async Task RunGitAsync(string workingDir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    private static async Task<(int ExitCode, string Output)> RunGitWithOutputAsync(string workingDir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output);
    }
}
