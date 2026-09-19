// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class AgentFileCommitterTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_ValidatesClosedStagingFileBeforePublication(bool destinationExists)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        if (destinationExists)
        {
            await File.WriteAllTextAsync(path, "original");
        }
        string? stagingPath = null;
        var validations = 0;

        await AgentFileCommitter.CommitAsync(
            path,
            destinationExists,
            async (stream, token) =>
            {
                stagingPath = Assert.IsType<FileStream>(stream).Name;
                Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(stagingPath));
                await stream.WriteAsync("replacement"u8.ToArray(), token);
                if (destinationExists)
                {
                    Assert.Equal("original", await File.ReadAllTextAsync(path, token));
                }
                else
                {
                    Assert.False(File.Exists(path));
                }
            },
            async token =>
            {
                validations++;
                // Exclusive access proves the staging writer was disposed before validation.
                await using var stream = new FileStream(stagingPath!, FileMode.Open, FileAccess.Read, FileShare.None);
                using var reader = new StreamReader(stream);
                Assert.Equal("replacement", await reader.ReadToEndAsync(token));
            },
            newFileMode: null,
            CancellationToken.None);

        Assert.Equal(1, validations);
        Assert.Equal("replacement", await File.ReadAllTextAsync(path));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task CommitAsync_CancelledBeforeStaging_DoesNotCreateDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = Path.Combine(workspace.Path, "missing", "payload");
        var writes = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentFileCommitter.CommitAsync(
            path,
            destinationExists: false,
            (_, _) =>
            {
                writes++;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            newFileMode: null,
            cancellation.Token));

        Assert.Equal(0, writes);
        Assert.Empty(workspace.WorkspaceRoot.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_CancelledBeforeReplacement_PreservesOriginalAndCleansStaging(bool duringWrite)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "working content");
        var originalTimestamp = File.GetLastWriteTimeUtc(path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentFileCommitter.CommitAsync(
            path,
            destinationExists: true,
            async (stream, token) =>
            {
                await stream.WriteAsync("partial replacement"u8.ToArray(), token);
                if (duringWrite)
                {
                    cancellation.Cancel();
                }
            },
            _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            newFileMode: null,
            cancellation.Token));

        Assert.Equal("working content", await File.ReadAllTextAsync(path));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task CommitAsync_ContentWriteFailure_PreservesOriginalAndOtherStagingFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "working content");
        var unownedPath = Path.Combine(workspace.Path, ".payload.aspire-other.tmp");
        await File.WriteAllTextAsync(unownedPath, "another invocation");

        var error = await Assert.ThrowsAsync<IOException>(() => AgentFileCommitter.CommitAsync(
            path,
            destinationExists: true,
            async (stream, token) =>
            {
                await stream.WriteAsync("partial replacement"u8.ToArray(), token);
                throw new IOException("content writer failed");
            },
            _ => Task.CompletedTask,
            newFileMode: null,
            CancellationToken.None));

        Assert.Equal("content writer failed", error.Message);
        Assert.Equal("working content", await File.ReadAllTextAsync(path));
        Assert.Equal("another invocation", await File.ReadAllTextAsync(unownedPath));
        Assert.Equal([".payload.aspire-other.tmp", "payload"],
            workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CommitAsync_ConcurrentModification_IsValidatedAfterStaging()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "original");
        var original = await File.ReadAllBytesAsync(path);

        var error = await Assert.ThrowsAsync<IOException>(() => AgentFileCommitter.CommitAsync(
            path,
            destinationExists: true,
            async (stream, token) =>
            {
                await stream.WriteAsync("replacement"u8.ToArray(), token);
                await File.WriteAllTextAsync(path, "concurrent edit", token);
            },
            async token =>
            {
                var current = await File.ReadAllBytesAsync(path, token);
                if (!original.AsSpan().SequenceEqual(current))
                {
                    throw new IOException(AgentConfigurationStrings.ConcurrentChange);
                }
            },
            newFileMode: null,
            CancellationToken.None));

        Assert.Equal(AgentConfigurationStrings.ConcurrentChange, error.Message);
        Assert.Equal("concurrent edit", await File.ReadAllTextAsync(path));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task CommitAsync_RepointedLink_IsValidatedBeforeReplacement()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var original = workspace.CreateDirectory("original");
        var replacement = workspace.CreateDirectory("replacement");
        var originalPath = Path.Combine(original.FullName, "payload");
        var replacementPath = Path.Combine(replacement.FullName, "payload");
        await File.WriteAllTextAsync(originalPath, "original destination");
        await File.WriteAllTextAsync(replacementPath, "replacement destination");
        var link = Path.Combine(workspace.Path, "linked");
        TestSymlinkHelper.TryCreateSymlink(link, original.FullName);
        var logicalPath = Path.Combine(link, "payload");
        var physicalPath = AgentConfigurationPath.Resolve(logicalPath);

        await Assert.ThrowsAsync<IOException>(() => AgentFileCommitter.CommitAsync(
            physicalPath,
            destinationExists: true,
            async (stream, token) =>
            {
                await stream.WriteAsync("new content"u8.ToArray(), token);
                Directory.Delete(link);
                Directory.CreateSymbolicLink(link, replacement.FullName);
            },
            _ =>
            {
                if (!AgentConfigurationPath.Comparer.Equals(physicalPath, AgentConfigurationPath.Resolve(logicalPath)))
                {
                    throw new IOException(AgentConfigurationStrings.ConcurrentChange);
                }
                return Task.CompletedTask;
            },
            newFileMode: null,
            CancellationToken.None));

        Assert.Equal("original destination", await File.ReadAllTextAsync(originalPath));
        Assert.Equal("replacement destination", await File.ReadAllTextAsync(replacementPath));
        Assert.Equal(["payload"], original.EnumerateFileSystemInfos().Select(static entry => entry.Name));
        Assert.Equal(["payload"], replacement.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task CommitAsync_NewDestinationAppearsAfterValidation_DoesNotOverwriteIt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");

        await Assert.ThrowsAsync<IOException>(() => AgentFileCommitter.CommitAsync(
            path,
            destinationExists: false,
            (stream, token) => stream.WriteAsync("our content"u8.ToArray(), token).AsTask(),
            token => File.WriteAllTextAsync(path, "concurrent creation", token),
            newFileMode: null,
            CancellationToken.None));

        Assert.Equal("concurrent creation", await File.ReadAllTextAsync(path));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task CommitAsync_ReplacementFails_DoesNotRemoveDestinationDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var destination = workspace.CreateDirectory("payload");
        var marker = Path.Combine(destination.FullName, "user-file");
        await File.WriteAllTextAsync(marker, "keep");

        var error = await Record.ExceptionAsync(() => AgentFileCommitter.CommitAsync(
            destination.FullName,
            destinationExists: true,
            (stream, token) => stream.WriteAsync("replacement"u8.ToArray(), token).AsTask(),
            _ => Task.CompletedTask,
            newFileMode: null,
            CancellationToken.None));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        Assert.Equal(["payload"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task CommitAsync_CleanupFailure_IsNotReportedAsSuccessOrRecursivelyDeleted()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "working content");
        string? stagingPath = null;

        var error = await Record.ExceptionAsync(() => AgentFileCommitter.CommitAsync(
            path,
            destinationExists: true,
            async (stream, token) =>
            {
                stagingPath = Assert.IsType<FileStream>(stream).Name;
                await stream.WriteAsync("replacement"u8.ToArray(), token);
            },
            async token =>
            {
                File.Delete(stagingPath!);
                Directory.CreateDirectory(stagingPath!);
                await File.WriteAllTextAsync(Path.Combine(stagingPath!, "unowned"), "keep", token);
            },
            newFileMode: null,
            CancellationToken.None));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal("working content", await File.ReadAllTextAsync(path));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(stagingPath!, "unowned")));
    }

    [Fact]
    public async Task CommitAsync_Replacement_PreservesUnixPermissionsIncludingUmaskFilteredBits()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permission preservation is not applicable on Windows.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "payload");
        await File.WriteAllTextAsync(path, "original");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(path, mode);
        string? stagingPath = null;

        await AgentFileCommitter.CommitAsync(
            path,
            destinationExists: true,
            async (stream, token) =>
            {
                stagingPath = Assert.IsType<FileStream>(stream).Name;
                await stream.WriteAsync("replacement"u8.ToArray(), token);
            },
            _ =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(mode, File.GetUnixFileMode(stagingPath!));
                }
                return Task.CompletedTask;
            },
            newFileMode: null,
            CancellationToken.None);

        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal("replacement", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task NativeWriter_ReadSetConflict_PreservesOriginalAndCleansStaging()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "settings.json");
        var policyPath = Path.Combine(workspace.Path, "policy.json");
        const string original = """{"existing":true}""";
        await File.WriteAllTextAsync(path, original);
        await File.WriteAllTextAsync(policyPath, """{"enabled":true}""");
        var target = new AgentConfigurationTarget(
            path,
            AgentConfigurationScope.Project,
            AgentAssetKind.Mcp,
            [AgentClientKind.CopilotCli],
            "test",
            async (root, context, token) =>
            {
                await context.ReadOptionalAsync(policyPath, token);
                await File.WriteAllTextAsync(policyPath, """{"enabled":false}""", token);
                root["added"] = true;
                return AgentConfigurationEdit.Applied("configured");
            });
        var writer = new AgentConfigurationWriter(NullLogger<AgentConfigurationWriter>.Instance);

        var results = await writer.ApplyAsync([target], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Blocked, result.Status);
        Assert.Equal(AgentConfigurationStrings.ConcurrentChange, result.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(["policy.json", "settings.json"],
            workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NativeWriter_NewFile_UsesPrivateUnixMode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "settings.json");
        var target = new AgentConfigurationTarget(
            path,
            AgentConfigurationScope.Project,
            AgentAssetKind.Mcp,
            [AgentClientKind.CopilotCli],
            "test",
            (root, _, _) =>
            {
                root["added"] = true;
                return Task.FromResult(AgentConfigurationEdit.Applied("configured"));
            });
        var writer = new AgentConfigurationWriter(NullLogger<AgentConfigurationWriter>.Instance);

        var results = await writer.ApplyAsync([target], CancellationToken.None);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.Equal(["settings.json"], workspace.WorkspaceRoot.EnumerateFileSystemInfos().Select(static entry => entry.Name));
    }

    [Fact]
    public async Task HookInstaller_ReplacementPreservesLinkPermissionsAndSubsequentNoOp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");
        var context = new CliExecutionContext(
            project, home, home, home, home, Path.Combine(home.FullName, "test.log"), "test", homeDirectory: home);
        var installer = new TelemetryHookInstaller(context, NullLogger<TelemetryHookInstaller>.Instance);
        var scripts = await installer.EnsureInstalledAsync(CancellationToken.None);
        var canonical = await File.ReadAllBytesAsync(scripts.PowerShellScriptPath);
        var physicalPath = Path.Combine(Path.GetDirectoryName(scripts.PowerShellScriptPath)!, "physical.ps1");
        await File.WriteAllTextAsync(physicalPath, "old script");
        File.Delete(scripts.PowerShellScriptPath);
        TestSymlinkHelper.TryCreateSymlink(scripts.PowerShellScriptPath, physicalPath, isDirectory: false);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(physicalPath, mode);
        }

        await installer.EnsureInstalledAsync(CancellationToken.None);

        Assert.Equal(canonical, await File.ReadAllBytesAsync(physicalPath));
        Assert.NotNull(new FileInfo(scripts.PowerShellScriptPath).LinkTarget);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(mode, File.GetUnixFileMode(physicalPath));
        }
        File.SetLastWriteTimeUtc(physicalPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(physicalPath);

        await installer.EnsureInstalledAsync(CancellationToken.None);

        Assert.Equal(canonical, await File.ReadAllBytesAsync(physicalPath));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(physicalPath));
        Assert.Equal(["physical.ps1", "track-telemetry.ps1", "track-telemetry.sh"],
            Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(physicalPath)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}
