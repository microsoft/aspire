// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class AppHostHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ComputeAuxiliarySocketPrefix_UsesAuxiPrefix()
    {
        // Arrange
        var appHostPath = Path.Combine("path", "to", "MyApp.AppHost.csproj");
        var homeDirectory = Path.Combine(Path.GetTempPath(), "testuser");

        // Act
        var socketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory);

        // Assert
        var fileName = Path.GetFileName(socketPrefix);
        Assert.StartsWith("auxi.sock.", fileName);
        
        // Verify the directory is under the backchannels folder
        var dir = Path.GetDirectoryName(socketPrefix);
        Assert.NotNull(dir);
        Assert.EndsWith("backchannels", dir);
    }

    [Fact]
    public void ComputeAuxiliarySocketPrefix_ProducesConsistentHash()
    {
        // Arrange
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/home/user";

        // Act
        var socketPrefix1 = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory);
        var socketPrefix2 = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory);

        // Assert - Same input should produce same prefix
        Assert.Equal(socketPrefix1, socketPrefix2);
    }

    [Fact]
    public void ComputeAuxiliarySocketPrefix_ProducesDifferentHashForDifferentAppHosts()
    {
        // Arrange
        var appHostPath1 = "/path/to/App1.AppHost.csproj";
        var appHostPath2 = "/path/to/App2.AppHost.csproj";
        var homeDirectory = "/home/user";

        // Act
        var socketPrefix1 = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath1, homeDirectory);
        var socketPrefix2 = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath2, homeDirectory);

        // Assert - Different inputs should produce different prefixes
        Assert.NotEqual(socketPrefix1, socketPrefix2);
    }

    [Fact]
    public void ComputeAuxiliarySocketPrefix_DoesNotUseReservedWindowsName()
    {
        // This test verifies that the socket path does not use "aux" which is a reserved
        // device name on Windows < 11 (from DOS days: CON, PRN, AUX, NUL, COM1-9, LPT1-9)
        
        // Arrange
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/home/user";

        // Act
        var socketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory);

        // Assert
        var fileName = Path.GetFileName(socketPrefix);
        
        // Should use "auxi" prefix, not "aux"
        Assert.StartsWith("auxi.sock.", fileName);
        Assert.DoesNotContain("aux.sock.", fileName);
    }

    [Fact]
    public void ComputeAuxiliarySocketPrefix_HashIs16Characters()
    {
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/home/user";

        var socketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory);

        // Format is: auxi.sock.{hash} where hash is 16 chars
        var fileName = Path.GetFileName(socketPrefix);
        var hash = fileName["auxi.sock.".Length..];
        Assert.Equal(16, hash.Length);
        Assert.Matches("^[a-f0-9]+$", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromNewFormat()
    {
        // Current format: auxi.sock.{hash}.{instanceHash}.{pid}
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.a1b2c3d4e5f6.12345";
        
        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);
        
        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromPreviousFormat()
    {
        // Previous format: auxi.sock.{hash}.{pid}
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.12345";

        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);

        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromOldFormat()
    {
        // Old format: auxi.sock.{hash}
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890";
        
        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);
        
        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromLegacyAuxFormat()
    {
        // Legacy format: aux.sock.{hash}
        var socketPath = "/home/user/.aspire/cli/backchannels/aux.sock.abc123def4567890";
        
        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);
        
        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ReturnsNullForUnrecognizedFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/unknown.sock.abc123";
        
        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);
        
        Assert.Null(hash);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ExtractsPidFromNewFormat()
    {
        // Current format: auxi.sock.{hash}.{instanceHash}.{pid}
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.a1b2c3d4e5f6.12345";
        
        var pid = AppHostHelper.ExtractPidFromSocketPath(socketPath);
        
        Assert.Equal(12345, pid);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ExtractsPidFromPreviousFormat()
    {
        // Previous format: auxi.sock.{hash}.{pid}
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.12345";

        var pid = AppHostHelper.ExtractPidFromSocketPath(socketPath);

        Assert.Equal(12345, pid);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ReturnsNullForOldFormat()
    {
        // Old format: auxi.sock.{hash} - no PID
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890";
        
        var pid = AppHostHelper.ExtractPidFromSocketPath(socketPath);
        
        Assert.Null(pid);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ReturnsNullForInvalidPid()
    {
        // Invalid PID (not a number)
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.notapid";
        
        var pid = AppHostHelper.ExtractPidFromSocketPath(socketPath);
        
        Assert.Null(pid);
    }

    [Fact]
    public void ProcessExists_ReturnsTrueForCurrentProcess()
    {
        var currentPid = Environment.ProcessId;
        
        var exists = AppHostHelper.ProcessExists(currentPid);
        
        Assert.True(exists);
    }

    [Fact]
    public void ProcessExists_ReturnsFalseForInvalidPid()
    {
        // Use a very high PID that's unlikely to exist
        var invalidPid = int.MaxValue - 1;
        
        var exists = AppHostHelper.ProcessExists(invalidPid);
        
        Assert.False(exists);
    }

    [Fact]
    public void FindMatchingSockets_ReturnsEmptyForNonExistentDirectory()
    {
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/nonexistent/home/directory";
        
        var sockets = AppHostHelper.FindMatchingSockets(appHostPath, homeDirectory);
        
        Assert.Empty(sockets);
    }

    [Fact]
    public void FindMatchingSockets_FindsMatchingSocketFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        
        // Get the hash by extracting from computed prefix
        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var hash = Path.GetFileName(prefix)["auxi.sock.".Length..];
        
        // Create matching socket files in both current and previous formats.
        var socket1 = Path.Combine(backchannelsDir, $"auxi.sock.{hash}.a1b2c3d4e5f6.12345");
        var socket2 = Path.Combine(backchannelsDir, $"auxi.sock.{hash}.67890");
        File.WriteAllText(socket1, "");
        File.WriteAllText(socket2, "");
        
        // Create a non-matching socket file (different hash)
        var otherSocket = Path.Combine(backchannelsDir, "auxi.sock.differenthash123.99999");
        File.WriteAllText(otherSocket, "");
        
        var sockets = AppHostHelper.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);
        
        Assert.Equal(2, sockets.Length);
        Assert.Contains(socket1, sockets);
        Assert.Contains(socket2, sockets);
        Assert.DoesNotContain(otherSocket, sockets);
    }

    [Fact]
    public void FindMatchingSockets_FindsOldFormatSocketsWithoutPid()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        
        // Get the hash by extracting from computed prefix
        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var hash = Path.GetFileName(prefix)["auxi.sock.".Length..];
        
        // Create old format socket (no PID) - for backward compatibility
        var oldFormatSocket = Path.Combine(backchannelsDir, $"auxi.sock.{hash}");
        File.WriteAllText(oldFormatSocket, "");
        
        // Create new format socket (with PID)
        var newFormatSocket = Path.Combine(backchannelsDir, $"auxi.sock.{hash}.12345");
        File.WriteAllText(newFormatSocket, "");
        
        var sockets = AppHostHelper.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);
        
        // Should find both old and new format
        Assert.Equal(2, sockets.Length);
        Assert.Contains(oldFormatSocket, sockets);
        Assert.Contains(newFormatSocket, sockets);
    }

    [Fact]
    public void FindMatchingSockets_DoesNotMatchSimilarHashes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        
        // Get the hash by extracting from computed prefix
        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var hash = Path.GetFileName(prefix)["auxi.sock.".Length..];
        
        // Create a socket with a hash that starts with the same chars but is different
        var similarSocket = Path.Combine(backchannelsDir, $"auxi.sock.{hash}xyz.12345");
        File.WriteAllText(similarSocket, "");
        
        var sockets = AppHostHelper.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);
        
        // Should NOT match the similar hash
        Assert.Empty(sockets);
    }

    [Fact]
    public void FindMatchingSockets_ReturnsEmptyWhenNoMatchingFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        
        // Create sockets for a DIFFERENT app host
        var otherSocket = Path.Combine(backchannelsDir, "auxi.sock.differenthash123.99999");
        File.WriteAllText(otherSocket, "");
        
        var sockets = AppHostHelper.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);
        
        Assert.Empty(sockets);
    }

    [Fact]
    public void CleanupOrphanedSockets_CleansUpBothOldAndNewFormatSockets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        
        // Get the hash
        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var hash = AppHostHelper.ExtractHashFromSocketPath(prefix)!;
        
        // Create old format socket (no PID) - should NOT be cleaned up (can't detect orphan without PID)
        var oldFormatSocket = Path.Combine(backchannelsDir, $"auxi.sock.{hash}");
        File.WriteAllText(oldFormatSocket, "");
        
        // Create new format socket with a dead PID (use int.MaxValue - 1 as unlikely to exist)
        var deadPid = int.MaxValue - 1;
        var orphanedSocket = Path.Combine(backchannelsDir, $"auxi.sock.{hash}.{deadPid}");
        File.WriteAllText(orphanedSocket, "");
        
        // Create new format socket with current PID (should NOT be deleted)
        var currentPid = Environment.ProcessId;
        var liveSocket = Path.Combine(backchannelsDir, $"auxi.sock.{hash}.{currentPid}");
        File.WriteAllText(liveSocket, "");
        
        var deleted = AppHostHelper.CleanupOrphanedSockets(backchannelsDir, hash, currentPid);
        
        // Should only delete the orphaned socket (dead PID)
        Assert.Equal(1, deleted);
        Assert.True(File.Exists(oldFormatSocket), "Old format socket should still exist (can't detect orphan)");
        Assert.False(File.Exists(orphanedSocket), "Orphaned socket should be deleted");
        Assert.True(File.Exists(liveSocket), "Live socket should still exist");
    }

    [Theory]
    [InlineData("10.0.0", true)]
    [InlineData("9.2.0", true)]
    [InlineData("9.3.0", true)]
    [InlineData("13.0.0-preview.1", true)]
    [InlineData("9.1.0", false)]
    [InlineData("8.0.0", false)]
    [InlineData("1.0.0", false)]
    public async Task CheckAppHostCompatibility_VersionCheck(string aspireVersion, bool expectedCompatible)
    {
        var runner = new TestDotNetCliRunner
        {
            GetAppHostInformationAsyncCallback = (_, _, _) => (0, true, aspireVersion)
        };
        var interactionService = new TestInteractionService();
        var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
        var projectFile = new FileInfo(Path.Combine(Path.GetTempPath(), "test.csproj"));
        var workingDirectory = new DirectoryInfo(Path.GetTempPath());

        var (isCompatible, _, returnedVersion) = await AppHostHelper.CheckAppHostCompatibilityAsync(
            runner, interactionService, projectFile, telemetry, workingDirectory, "test.log", CancellationToken.None);

        Assert.Equal(expectedCompatible, isCompatible);
        Assert.Equal(aspireVersion, returnedVersion);
    }
}
