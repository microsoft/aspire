// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class CacheCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CacheCommand_WithoutSubcommand_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("cache");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task CacheCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("cache --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task CacheClear_ClearsPackagesDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "packages"));
        var restoreDir = packagesDir.CreateSubdirectory("restore").CreateSubdirectory("ABC123");
        File.WriteAllText(Path.Combine(restoreDir.FullName, "test.dll"), "fake");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.PackagesDirectory = packagesDir;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("cache clear");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(File.Exists(Path.Combine(restoreDir.FullName, "test.dll")));
    }

    [Fact]
    public async Task CacheClear_HandlesNonExistentPackagesDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "packages-nonexistent"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.PackagesDirectory = packagesDir;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("cache clear");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public void ClearDirectoryContents_DeletesFilesAndSubdirectories()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"aspire-test-{Guid.NewGuid():N}"));
        try
        {
            tempDir.Create();
            var subDir = tempDir.CreateSubdirectory("nested");
            File.WriteAllText(Path.Combine(tempDir.FullName, "root.txt"), "root");
            File.WriteAllText(Path.Combine(subDir.FullName, "nested.txt"), "nested");

            var deleted = CacheCommand.ClearCommand.ClearDirectoryContents(tempDir);

            Assert.Equal(2, deleted);
            Assert.Empty(tempDir.GetFiles("*", SearchOption.AllDirectories));
            Assert.Empty(tempDir.GetDirectories());
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public void ClearDirectoryContents_RespectsSkipPredicate()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"aspire-test-{Guid.NewGuid():N}"));
        try
        {
            tempDir.Create();
            var keepFile = Path.Combine(tempDir.FullName, "keep.txt");
            var deleteFile = Path.Combine(tempDir.FullName, "delete.txt");
            File.WriteAllText(keepFile, "keep");
            File.WriteAllText(deleteFile, "delete");

            var deleted = CacheCommand.ClearCommand.ClearDirectoryContents(
                tempDir,
                skipFile: f => f.Name == "keep.txt");

            Assert.Equal(1, deleted);
            Assert.True(File.Exists(keepFile));
            Assert.False(File.Exists(deleteFile));
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public void ClearDirectoryContents_ReturnsZero_WhenDirectoryDoesNotExist()
    {
        var nonExistent = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"aspire-test-nonexistent-{Guid.NewGuid():N}"));

        var deleted = CacheCommand.ClearCommand.ClearDirectoryContents(nonExistent);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public void ClearDirectoryContents_ReturnsZero_WhenNull()
    {
        var deleted = CacheCommand.ClearCommand.ClearDirectoryContents(null);

        Assert.Equal(0, deleted);
    }
}
