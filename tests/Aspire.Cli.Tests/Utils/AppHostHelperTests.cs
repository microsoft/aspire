// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Aspire.Hosting.Backchannel;

namespace Aspire.Cli.Tests.Utils;

public class AppHostHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ComputeAuxiliarySocketPrefix_UsesCompactBackchannelDirectory()
    {
        var appHostPath = Path.Combine("path", "to", "MyApp.AppHost.csproj");
        var homeDirectory = Path.Combine(Path.GetTempPath(), "testuser");

        var socketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory);

        var fileName = Path.GetFileName(socketPrefix);
        Assert.Matches("^[A-Za-z0-9_-]{11}$", fileName);

        var dir = Path.GetDirectoryName(socketPrefix);
        Assert.NotNull(dir);
        Assert.Equal(Path.Combine(homeDirectory, ".aspire", "cli", "bch"), dir);
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
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/home/user";

        var socketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory);

        var fileName = Path.GetFileName(socketPrefix);
        Assert.Equal(11, fileName.Length);
        Assert.DoesNotContain("auxi.sock.", fileName);
        Assert.DoesNotContain("aux.sock.", fileName);
    }

    [Fact]
    public void ComputeAuxiliarySocketPrefix_AppHostIdIs11Base64UrlCharacters()
    {
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/home/user";

        var socketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, homeDirectory);

        var fileName = Path.GetFileName(socketPrefix);
        Assert.Equal(11, fileName.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", fileName);
    }

    [Fact]
    public void ComputeSocketPath_UsesUtf8ByteCountLimitForNonAsciiHomeDirectory()
    {
        var homeDirectory = @"C:\Users\TanakaTarou（田中太郎）";
        var appHostPath = @"C:\src\MyApp.AppHost\MyApp.AppHost.csproj";
        var processId = 26688;
        var oldSocketPath = Path.Combine(homeDirectory, ".aspire", "cli", "backchannels", "auxi.sock.3a579b6853b74a71.fee67dd76369.26688");

        var socketPath = BackchannelConstants.ComputeSocketPath(appHostPath, homeDirectory, processId);

        Assert.True(
            BackchannelConstants.GetSocketPathByteCountIncludingNull(oldSocketPath) > BackchannelConstants.GetMaxSocketPathBytesIncludingNull(),
            $"The legacy path should exceed the platform byte limit for this regression case: {oldSocketPath}");
        Assert.True(
            BackchannelConstants.GetSocketPathByteCountIncludingNull(socketPath) <= BackchannelConstants.GetMaxSocketPathBytesIncludingNull(),
            $"The compact path should fit the platform byte limit: {socketPath}");
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromCompactFormat()
    {
        var socketPath = "/home/user/.aspire/cli/bch/AbCdEfGhIjkLmNoPqRs.12345";

        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);

        Assert.Equal("AbCdEfGhIjk", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromLegacyCurrentFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.a1b2c3d4e5f6.12345";

        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);

        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromPreviousFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.12345";

        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);

        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromOldFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890";

        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);

        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromLegacyAuxFormat()
    {
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
        // Legacy current format: auxi.sock.{hash}.{instanceHash}.{pid}
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.a1b2c3d4e5f6.12345";

        var pid = AppHostHelper.ExtractPidFromSocketPath(socketPath);

        Assert.Equal(12345, pid);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ExtractsPidFromPreviousFormat()
    {
        // Legacy previous format: auxi.sock.{hash}.{pid}
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
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var appHostId = Path.GetFileName(prefix);

        var socket1 = Path.Combine(backchannelsDir, $"{appHostId}a1b2C3d4.12345");
        var socket2 = Path.Combine(backchannelsDir, $"{appHostId}Z9y8X7w6.67890");
        File.WriteAllText(socket1, "");
        File.WriteAllText(socket2, "");

        var otherSocket = Path.Combine(backchannelsDir, "differentId1a1b2C3d4.99999");
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
        var legacyBackchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(legacyBackchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        var hash = AppHostHelper.ComputeLegacyHashes(appHostPath)[0];

        var oldFormatSocket = Path.Combine(legacyBackchannelsDir, $"auxi.sock.{hash}");
        File.WriteAllText(oldFormatSocket, "");

        var legacyPidSocket = Path.Combine(legacyBackchannelsDir, $"auxi.sock.{hash}.12345");
        File.WriteAllText(legacyPidSocket, "");

        var sockets = AppHostHelper.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);

        // Should find both old and new format
        Assert.Equal(2, sockets.Length);
        Assert.Contains(oldFormatSocket, sockets);
        Assert.Contains(legacyPidSocket, sockets);
    }

    [Fact]
    public void FindMatchingSockets_DoesNotMatchSimilarHashes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var appHostId = Path.GetFileName(prefix);

        // 8 base64url chars but missing the '.' separator before PID
        var badSeparator = Path.Combine(backchannelsDir, $"{appHostId}AbCdEfGhX12345");
        File.WriteAllText(badSeparator, "");

        // Correct structure but non-integer PID
        var badPid = Path.Combine(backchannelsDir, $"{appHostId}AbCdEfGh.notapid");
        File.WriteAllText(badPid, "");

        var sockets = AppHostHelper.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);

        // Should NOT match the similar hash
        Assert.Empty(sockets);
    }

    [Fact]
    public void FindMatchingSockets_ReturnsEmptyWhenNoMatchingFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        // Create sockets for a DIFFERENT app host
        var otherSocket = Path.Combine(backchannelsDir, "differentId1a1b2C3d4.99999");
        File.WriteAllText(otherSocket, "");

        var sockets = AppHostHelper.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);

        Assert.Empty(sockets);
    }

    [Fact]
    public void CleanupOrphanedSockets_CleansUpBothOldAndNewFormatSockets()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var appHostId = AppHostHelper.ExtractHashFromSocketPath(prefix)!;

        var oldFormatSocket = Path.Combine(backchannelsDir, appHostId);
        File.WriteAllText(oldFormatSocket, "");

        var deadPid = int.MaxValue - 1;
        var orphanedSocket = Path.Combine(backchannelsDir, $"{appHostId}a1b2C3d4.{deadPid}");
        File.WriteAllText(orphanedSocket, "");

        var currentPid = Environment.ProcessId;
        var liveSocket = Path.Combine(backchannelsDir, $"{appHostId}Z9y8X7w6.{currentPid}");
        File.WriteAllText(liveSocket, "");

        var deleted = AppHostHelper.CleanupOrphanedSockets(backchannelsDir, appHostId, currentPid);

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

        var (isCompatible, returnedVersion) = await AppHostHelper.CheckAppHostCompatibilityAsync(
            runner, interactionService, projectFile, telemetry, workingDirectory, "test.log", CancellationToken.None);

        Assert.Equal(expectedCompatible, isCompatible);
        Assert.Equal(aspireVersion, returnedVersion);
    }

    [Fact]
    public void ComputeLegacyHashes_IncludesDriveLetterOnlyHashSharedAcrossCasings()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "Drive-letter legacy fallback behavior only applies on Windows.");

        var upperDrivePath = @"C:\Path\To\MyApp.AppHost.csproj";
        var lowerDrivePath = @"c:\Path\To\MyApp.AppHost.csproj";

        var upperHashes = AppHostHelper.ComputeLegacyHashes(upperDrivePath);
        var lowerHashes = AppHostHelper.ComputeLegacyHashes(lowerDrivePath);

        // The drive-letter-only normalized hash (produced by AppHost versions that only
        // upper-cased the drive letter) must appear in both arrays so sockets created by
        // those AppHosts are still discoverable regardless of which drive-letter casing
        // the current caller has.
        var shared = upperHashes.Intersect(lowerHashes, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(shared);

        // The current (full-uppercase on Windows) hash must also be shared because the
        // entire path is now normalized.
        Assert.Equal(upperHashes[0], lowerHashes[0]);
    }

    [Fact]
    public void ComputeLegacyHash_ReturnsNullOnNonWindowsWhenPathUnchanged()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Non-Windows behavior is validated by this test.");

        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var legacyHash = AppHostHelper.ComputeLegacyHash(appHostPath);

        Assert.Null(legacyHash);
    }

    [Fact]
    public void ComputeHash_IsCaseInsensitiveAcrossFullPathOnWindows()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "Full-path normalization only applies on Windows.");

        var upper = @"C:\Foo\Bar\App.AppHost.csproj";
        var mixed = @"c:\foo\BAR\app.apphost.CSPROJ";

        Assert.Equal(BackchannelConstants.ComputeHash(upper), BackchannelConstants.ComputeHash(mixed));
        Assert.Equal(BackchannelConstants.ComputeAppHostId(upper), BackchannelConstants.ComputeAppHostId(mixed));
    }

    [Fact]
    public void FindMatchingSockets_FindsCompactSocketAcrossPathCasing()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "Full-path normalization only applies on Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var upperPath = @"C:\Foo\Bar\App.AppHost.csproj";
        var mixedPath = @"c:\foo\BAR\app.apphost.CSPROJ";

        var appHostId = BackchannelConstants.ComputeAppHostId(upperPath);
        var socket = Path.Combine(backchannelsDir, $"{appHostId}a1b2C3d4.12345");
        File.WriteAllText(socket, "");

        var found = AppHostHelper.FindMatchingSockets(mixedPath, workspace.WorkspaceRoot.FullName);
        Assert.Single(found);
        Assert.Contains(socket, found);
    }

    [Fact]
    public void FindMatchingSockets_FindsSocketsCreatedWithDifferentDriveLetterCasing()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "Drive letter normalization only applies on Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        // Simulate the real-world mismatch: FileInfo.FullName yields an uppercase drive letter
        // (e.g. "C:\...") while MSBuild metadata may yield a lowercase one (e.g. "c:\...").
        // Only the drive letter casing differs; the rest of the path is identical.
        var upperDrivePath = @"C:\Development\MyApp\MyApp.AppHost.csproj";
        var lowerDrivePath = @"c:\Development\MyApp\MyApp.AppHost.csproj";

        // Both should produce the same AppHost ID after drive-letter normalization.
        var upperPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(upperDrivePath, workspace.WorkspaceRoot.FullName);
        var lowerPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(lowerDrivePath, workspace.WorkspaceRoot.FullName);
        Assert.Equal(upperPrefix, lowerPrefix);

        var appHostId = Path.GetFileName(upperPrefix);

        var socket = Path.Combine(backchannelsDir, $"{appHostId}a1b2C3d4.12345");
        File.WriteAllText(socket, "");

        // Both path variants should find the socket
        var fromUpper = AppHostHelper.FindMatchingSockets(upperDrivePath, workspace.WorkspaceRoot.FullName);
        var fromLower = AppHostHelper.FindMatchingSockets(lowerDrivePath, workspace.WorkspaceRoot.FullName);

        Assert.Single(fromUpper);
        Assert.Single(fromLower);
        Assert.Contains(socket, fromUpper);
        Assert.Contains(socket, fromLower);
    }

    [Fact]
    public void FindMatchingSockets_LegacyHashFindsSocketsFromOlderAppHost()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "Legacy hash divergence only occurs on Windows where drive-letter casing is normalized.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(backchannelsDir);

        // A path with a lowercase drive letter produces a legacy hash that differs from the
        // normalized hash (which has an uppercase drive letter).
        var appHostPath = @"c:\Development\MyApp\MyApp.AppHost.csproj";
        var legacyHash = AppHostHelper.ComputeLegacyHash(appHostPath);
        Assert.NotNull(legacyHash);

        // Create a socket using the legacy (pre-normalization) hash, as an older AppHost would
        var legacySocket = Path.Combine(backchannelsDir, $"auxi.sock.{legacyHash}.a1b2c3d4e5f6.99999");
        File.WriteAllText(legacySocket, "");

        var currentHash = AppHostHelper.ComputeLegacyHashes(appHostPath)[0];
        Assert.NotEqual(currentHash, legacyHash);

        // FindMatchingSockets should still find the legacy socket via fallback
        var found = AppHostHelper.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);
        Assert.Single(found);
        Assert.Contains(legacySocket, found);
    }
}
