// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Aspire.Cli.Scaffolding;

namespace Aspire.Cli.Tests.Scaffolding;

public class GitIgnoreFileTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task WriteAllTextAtomicallyAsync_CancellationPreservesExistingFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        await File.WriteAllTextAsync(path, "keep-me\n");
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GitIgnoreFile.WriteAllTextAtomicallyAsync(
                path,
                new string('x', 64 * 1024),
                cancellationTokenSource.Token));

        Assert.Equal("keep-me\n", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(workspace.WorkspaceRoot.FullName, ".gitignore.tmp-*"));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_ReclaimsStaleTemporaryFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        var orphanPath = path + ".tmp-" + new string('0', 32);
        await File.WriteAllTextAsync(orphanPath, "orphan\n");
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UnixEpoch);

        await GitIgnoreFile.WriteAllTextAtomicallyAsync(
            path,
            "replacement\n",
            TestContext.Current.CancellationToken);

        Assert.Equal("replacement\n", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(orphanPath));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_PreservesActiveStaleTemporaryFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        var activePath = path + ".tmp-" + new string('1', 32);
        await File.WriteAllTextAsync(activePath, "active\n");
        File.SetLastWriteTimeUtc(activePath, DateTime.UnixEpoch);
        using var activeWriter = new FileStream(activePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await GitIgnoreFile.WriteAllTextAtomicallyAsync(
            path,
            "replacement\n",
            TestContext.Current.CancellationToken);

        Assert.Equal("replacement\n", await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(activePath));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_PreservesRecentTemporaryFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        var recentPath = path + ".tmp-" + new string('2', 32);
        await File.WriteAllTextAsync(recentPath, "recent\n");
        File.SetLastWriteTimeUtc(recentPath, DateTime.UtcNow.AddHours(1));

        await GitIgnoreFile.WriteAllTextAtomicallyAsync(
            path,
            "replacement\n",
            TestContext.Current.CancellationToken);

        Assert.Equal("replacement\n", await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(recentPath));
    }

    [Fact]
    public async Task WriteAllTextAtomicallyAsync_PreservesUnrelatedSiblingFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        string[] unrelatedPaths =
        [
            path + ".tmp-" + new string('0', 31),
            path + ".tmp-" + new string('A', 32),
            path + ".tmp-" + new string('0', 32) + "-extra",
            Path.Combine(workspace.WorkspaceRoot.FullName, "other.tmp-" + new string('0', 32))
        ];

        foreach (var unrelatedPath in unrelatedPaths)
        {
            await File.WriteAllTextAsync(unrelatedPath, "unrelated\n");
            File.SetLastWriteTimeUtc(unrelatedPath, DateTime.UnixEpoch);
        }

        await GitIgnoreFile.WriteAllTextAtomicallyAsync(
            path,
            "replacement\n",
            TestContext.Current.CancellationToken);

        Assert.Equal("replacement\n", await File.ReadAllTextAsync(path));
        Assert.All(unrelatedPaths, unrelatedPath => Assert.True(File.Exists(unrelatedPath)));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteAllTextAtomicallyAsync_RefusesReadOnlyUnixFile()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            Assert.Skip("Requires a non-privileged process on a platform with Unix file modes.");
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        await File.WriteAllTextAsync(path, "keep-me\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => GitIgnoreFile.WriteAllTextAtomicallyAsync(
                    path,
                    "replacement\n",
                    TestContext.Current.CancellationToken));

            Assert.Equal("keep-me\n", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(workspace.WorkspaceRoot.FullName, ".gitignore.tmp-*"));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
