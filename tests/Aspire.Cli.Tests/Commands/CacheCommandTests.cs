// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
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

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
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

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(Path.Combine(restoreDir.FullName, "test.dll")));
    }

    [Fact]
    public async Task CacheClear_ClearsAppHostInfoDiskCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostInfoCacheDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "apphost-info"));
        appHostInfoCacheDir.Create();
        var cacheEntry = Path.Combine(appHostInfoCacheDir.FullName, "entry.json");
        await File.WriteAllTextAsync(cacheEntry, "{}").DefaultTimeout();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("cache clear");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(cacheEntry));
        Assert.False(appHostInfoCacheDir.Exists);
    }

    [Fact]
    public async Task CacheClear_ClearsStagingNuGetPackagesCache()
    {
        // Pins that `aspire cache clear` wipes the SHA-keyed staging NuGet package caches under
        // <ASPIRE_HOME>/.nugetpackages — produced by PrebuiltAppHostServer's temporary
        // nuget.config for the staging channel. Without this, a wedged staging restore can only
        // be recovered by manual filesystem surgery.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var executionContext = provider.GetRequiredService<CliExecutionContext>();

        var stagingCacheRoot = new DirectoryInfo(
            CliPathHelper.GetStagingNuGetPackagesDirectory(executionContext.AspireHomeDirectory));
        var firstBuildCache = stagingCacheRoot.CreateSubdirectory("deadbeef").CreateSubdirectory("aspire.hosting").CreateSubdirectory("13.4.0");
        var secondBuildCache = stagingCacheRoot.CreateSubdirectory("cafef00d").CreateSubdirectory("aspire.hosting").CreateSubdirectory("13.4.0");
        await File.WriteAllTextAsync(Path.Combine(firstBuildCache.FullName, "Aspire.Hosting.dll"), "fake").DefaultTimeout();
        await File.WriteAllTextAsync(Path.Combine(secondBuildCache.FullName, "Aspire.Hosting.dll"), "fake").DefaultTimeout();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("cache clear");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        // SHA-keyed subdirectories should be gone; the parent stays so the next staging restore
        // can populate a fresh cache without recreating the .nugetpackages root.
        Assert.False(Directory.Exists(Path.Combine(stagingCacheRoot.FullName, "deadbeef")));
        Assert.False(Directory.Exists(Path.Combine(stagingCacheRoot.FullName, "cafef00d")));
    }

    [Fact]
    public async Task CacheClear_HandlesMissingStagingNuGetPackagesCache()
    {
        // Common case for fresh installs and non-staging users: the staging cache root simply
        // doesn't exist yet. The command must still succeed.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var executionContext = provider.GetRequiredService<CliExecutionContext>();

        var stagingCacheRoot = new DirectoryInfo(
            CliPathHelper.GetStagingNuGetPackagesDirectory(executionContext.AspireHomeDirectory));
        Assert.False(stagingCacheRoot.Exists);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("cache clear");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
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

        Assert.Equal(CliExitCodes.Success, exitCode);
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
