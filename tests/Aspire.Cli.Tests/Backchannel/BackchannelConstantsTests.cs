// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Backchannel;

public class BackchannelConstantsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ComputeSocketPrefix_UsesCompactBackchannelDirectory()
    {
        var appHostPath = Path.Combine("path", "to", "MyApp.AppHost.csproj");
        var homeDirectory = Path.Combine(Path.GetTempPath(), "testuser");

        var socketPrefix = BackchannelConstants.ComputeSocketPrefix(appHostPath, homeDirectory);

        var fileName = Path.GetFileName(socketPrefix);
        Assert.Matches("^[A-Za-z0-9_-]{11}$", fileName);

        var dir = Path.GetDirectoryName(socketPrefix);
        Assert.NotNull(dir);
        Assert.Equal(Path.Combine(homeDirectory, ".aspire", "cli", "bch"), dir);
    }

    [Fact]
    public void ComputeSocketPrefix_ProducesConsistentHash()
    {
        // Arrange
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/home/user";

        // Act
        var socketPrefix1 = BackchannelConstants.ComputeSocketPrefix(appHostPath, homeDirectory);
        var socketPrefix2 = BackchannelConstants.ComputeSocketPrefix(appHostPath, homeDirectory);

        // Assert - Same input should produce same prefix
        Assert.Equal(socketPrefix1, socketPrefix2);
    }

    [Fact]
    public void ComputeSocketPrefix_ProducesDifferentHashForDifferentAppHosts()
    {
        // Arrange
        var appHostPath1 = "/path/to/App1.AppHost.csproj";
        var appHostPath2 = "/path/to/App2.AppHost.csproj";
        var homeDirectory = "/home/user";

        // Act
        var socketPrefix1 = BackchannelConstants.ComputeSocketPrefix(appHostPath1, homeDirectory);
        var socketPrefix2 = BackchannelConstants.ComputeSocketPrefix(appHostPath2, homeDirectory);

        // Assert - Different inputs should produce different prefixes
        Assert.NotEqual(socketPrefix1, socketPrefix2);
    }

    [Fact]
    public void ComputeSocketPrefix_DoesNotUseReservedWindowsName()
    {
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/home/user";

        var socketPrefix = BackchannelConstants.ComputeSocketPrefix(appHostPath, homeDirectory);

        var fileName = Path.GetFileName(socketPrefix);
        Assert.Equal(11, fileName.Length);
        Assert.DoesNotContain("auxi.sock.", fileName);
        Assert.DoesNotContain("aux.sock.", fileName);
    }

    [Fact]
    public void ComputeSocketPrefix_AppHostIdIs11Base64UrlCharacters()
    {
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/home/user";

        var socketPrefix = BackchannelConstants.ComputeSocketPrefix(appHostPath, homeDirectory);

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

        var hash = BackchannelConstants.ExtractHash(socketPath);

        Assert.Equal("AbCdEfGhIjk", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromLegacyCurrentFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.a1b2c3d4e5f6.12345";

        var hash = BackchannelConstants.ExtractHash(socketPath);

        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromPreviousFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.12345";

        var hash = BackchannelConstants.ExtractHash(socketPath);

        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromOldFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890";

        var hash = BackchannelConstants.ExtractHash(socketPath);

        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ExtractsHashFromLegacyAuxFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/aux.sock.abc123def4567890";

        var hash = BackchannelConstants.ExtractHash(socketPath);

        Assert.Equal("abc123def4567890", hash);
    }

    [Fact]
    public void ExtractHashFromSocketPath_ReturnsNullForUnrecognizedFormat()
    {
        var socketPath = "/home/user/.aspire/cli/backchannels/unknown.sock.abc123";

        var hash = BackchannelConstants.ExtractHash(socketPath);

        Assert.Null(hash);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ExtractsPidFromNewFormat()
    {
        // Legacy current format: auxi.sock.{hash}.{instanceHash}.{pid}
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.a1b2c3d4e5f6.12345";

        var pid = BackchannelConstants.ExtractPid(socketPath);

        Assert.Equal(12345, pid);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ExtractsPidFromPreviousFormat()
    {
        // Legacy previous format: auxi.sock.{hash}.{pid}
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.12345";

        var pid = BackchannelConstants.ExtractPid(socketPath);

        Assert.Equal(12345, pid);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ReturnsNullForOldFormat()
    {
        // Old format: auxi.sock.{hash} - no PID
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890";

        var pid = BackchannelConstants.ExtractPid(socketPath);

        Assert.Null(pid);
    }

    [Fact]
    public void ExtractPidFromSocketPath_ReturnsNullForInvalidPid()
    {
        // Invalid PID (not a number)
        var socketPath = "/home/user/.aspire/cli/backchannels/auxi.sock.abc123def4567890.notapid";

        var pid = BackchannelConstants.ExtractPid(socketPath);

        Assert.Null(pid);
    }

    [Fact]
    public void ProcessExists_ReturnsTrueForCurrentProcess()
    {
        var currentPid = Environment.ProcessId;

        var exists = BackchannelConstants.ProcessExists(currentPid);

        Assert.True(exists);
    }

    [Fact]
    public void ProcessExists_ReturnsFalseForInvalidPid()
    {
        // Use a very high PID that's unlikely to exist
        var invalidPid = int.MaxValue - 1;

        var exists = BackchannelConstants.ProcessExists(invalidPid);

        Assert.False(exists);
    }

    [Fact]
    public void FindMatchingSockets_ReturnsEmptyForNonExistentDirectory()
    {
        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        var homeDirectory = "/nonexistent/home/directory";

        var sockets = BackchannelConstants.FindMatchingSockets(appHostPath, homeDirectory);

        Assert.Empty(sockets);
    }

    [Fact]
    public void FindMatchingSockets_FindsMatchingSocketFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        var prefix = BackchannelConstants.ComputeSocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var appHostId = Path.GetFileName(prefix);

        var socket1 = Path.Combine(backchannelsDir, $"{appHostId}a1b2C3d4.12345");
        var socket2 = Path.Combine(backchannelsDir, $"{appHostId}Z9y8X7w6.67890");
        File.WriteAllText(socket1, "");
        File.WriteAllText(socket2, "");

        var otherSocket = Path.Combine(backchannelsDir, "differentId1a1b2C3d4.99999");
        File.WriteAllText(otherSocket, "");

        var sockets = BackchannelConstants.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);

        Assert.Equal(2, sockets.Length);
        Assert.Contains(socket1, sockets);
        Assert.Contains(socket2, sockets);
        Assert.DoesNotContain(otherSocket, sockets);
    }

    [Fact]
    public void FindMatchingSockets_FindsOldFormatSocketsWithoutPid()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var legacyBackchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(legacyBackchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        var hash = BackchannelConstants.ComputeLegacyHashes(appHostPath)[0];

        var oldFormatSocket = Path.Combine(legacyBackchannelsDir, $"auxi.sock.{hash}");
        File.WriteAllText(oldFormatSocket, "");

        var legacyPidSocket = Path.Combine(legacyBackchannelsDir, $"auxi.sock.{hash}.12345");
        File.WriteAllText(legacyPidSocket, "");

        var sockets = BackchannelConstants.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);

        // Should find both old and new format
        Assert.Equal(2, sockets.Length);
        Assert.Contains(oldFormatSocket, sockets);
        Assert.Contains(legacyPidSocket, sockets);
    }

    [Fact]
    public void FindMatchingSockets_DoesNotMatchSimilarHashes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        var prefix = BackchannelConstants.ComputeSocketPrefix(appHostPath, workspace.WorkspaceRoot.FullName);
        var appHostId = Path.GetFileName(prefix);

        // 8 base64url chars but missing the '.' separator before PID
        var badSeparator = Path.Combine(backchannelsDir, $"{appHostId}AbCdEfGhX12345");
        File.WriteAllText(badSeparator, "");

        // Correct structure but non-integer PID
        var badPid = Path.Combine(backchannelsDir, $"{appHostId}AbCdEfGh.notapid");
        File.WriteAllText(badPid, "");

        var sockets = BackchannelConstants.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);

        // Should NOT match the similar hash
        Assert.Empty(sockets);
    }

    [Fact]
    public void FindMatchingSockets_ReturnsEmptyWhenNoMatchingFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";

        // Create sockets for a DIFFERENT app host
        var otherSocket = Path.Combine(backchannelsDir, "differentId1a1b2C3d4.99999");
        File.WriteAllText(otherSocket, "");

        var sockets = BackchannelConstants.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);

        Assert.Empty(sockets);
    }

    [Fact]
    public void FindSockets_RemovesDeadPidSocketsAndKeepsLiveAndPidlessSockets()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var appHostPath = "/path/to/MyApp.AppHost.csproj";
        // FindSockets resolves symlinks (which canonicalizes via Path.GetFullPath)
        // before hashing, so the socket files must be keyed off the same resolved path. On Windows
        // Path.GetFullPath roots the drive-less "/path/to/..." to "C:\path\to\...", giving a different
        // hash than the raw string; resolving here keeps both sides consistent across all platforms.
        var resolvedAppHostPath = PathNormalizer.ResolveSymlinks(appHostPath);
        var prefix = BackchannelConstants.ComputeSocketPrefix(resolvedAppHostPath, workspace.WorkspaceRoot.FullName);
        var appHostId = Path.GetFileName(prefix);
        var deadPid = int.MaxValue - 1;
        var currentPid = Environment.ProcessId;

        var orphanedSocket = Path.Combine(backchannelsDir, $"{appHostId}a1b2C3d4.{deadPid}");
        var liveSocket = Path.Combine(backchannelsDir, $"{appHostId}Z9y8X7w6.{currentPid}");
        var pidlessSocket = Path.Combine(backchannelsDir, appHostId);
        File.WriteAllText(orphanedSocket, "");
        File.WriteAllText(liveSocket, "");
        File.WriteAllText(pidlessSocket, "");

        var remainingSockets = AppHostSocketManager.FindSockets(
            appHostPath,
            workspace.WorkspaceRoot.FullName,
            currentPid,
            NullLogger.Instance)
            .Select(socket => socket.SocketPath);

        Assert.Collection(
            remainingSockets.Order(StringComparer.Ordinal),
            socket => Assert.Equal(pidlessSocket, socket),
            socket => Assert.Equal(liveSocket, socket));
        Assert.False(File.Exists(orphanedSocket));
        Assert.True(File.Exists(liveSocket));
        Assert.True(File.Exists(pidlessSocket));
    }

    [Fact]
    public void FindSockets_WithSymlinkedPath_MatchesCurrentAndHistoricalSockets()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var symlinkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");
        Directory.CreateSymbolicLink(symlinkDirectory, realDirectory.FullName);

        var projectFileViaSymlink = Path.Combine(symlinkDirectory, "TestAppHost.csproj");
        File.WriteAllText(projectFileViaSymlink, "<Project />");

        var socketKeyPaths = new[]
        {
            PathNormalizer.ResolveToFilesystemPath(projectFileViaSymlink),
            PathNormalizer.ResolveSymlinks(projectFileViaSymlink),
            projectFileViaSymlink
        }.Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(PathNormalizer.ResolveToFilesystemPath(projectFileViaSymlink), socketKeyPaths[0]);
        Assert.Equal(projectFileViaSymlink, socketKeyPaths[^1]);
        Assert.Contains(PathNormalizer.ResolveSymlinks(projectFileViaSymlink), socketKeyPaths);

        var currentPid = Environment.ProcessId;
        var backchannelsDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDirectory);
        var expectedSockets = socketKeyPaths.Select((path, index) =>
        {
            var appHostId = Path.GetFileName(
                BackchannelConstants.ComputeSocketPrefix(path, workspace.WorkspaceRoot.FullName));
            var socketPath = Path.Combine(backchannelsDirectory, $"{appHostId}A{index:D7}.{currentPid}");
            File.WriteAllText(socketPath, "");
            return socketPath;
        }).ToArray();

        var remainingSockets = AppHostSocketManager.FindSockets(
            projectFileViaSymlink,
            workspace.WorkspaceRoot.FullName,
            currentPid,
            NullLogger.Instance)
            .Select(socket => socket.SocketPath);

        Assert.Equal(expectedSockets.Order(StringComparer.Ordinal), remainingSockets.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ComputeSocketPrefix_ResolvedSymlinkPath_MatchesRealTargetPrefix()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Symlink resolution test only runs on Linux/macOS where unprivileged symlink creation is reliable.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.WorkspaceRoot.FullName;

        // Build a directory symlink ("link" -> "real") and reference the same on-disk AppHost
        // through both paths. This reproduces the macOS temp-path shape where /var/folders/...
        // is a symlink to /private/var/folders/..., so the symlinked and real paths are the same
        // file but differ textually.
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var symlinkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");
        Directory.CreateSymbolicLink(symlinkDirectory, realDirectory.FullName);

        var realProjectPath = Path.Combine(realDirectory.FullName, "TestAppHost.csproj");
        File.WriteAllText(realProjectPath, "<Project />");
        var projectFileViaSymlink = Path.Combine(symlinkDirectory, "TestAppHost.csproj");

        // The AppHost keys its auxiliary backchannel socket on the filesystem-canonical path, so the CLI
        // must resolve the symlinked path to arrive at the same socket prefix as the real target.
        var resolvedViaSymlink = PathNormalizer.ResolveToFilesystemPath(projectFileViaSymlink);
        var resolvedRealTarget = PathNormalizer.ResolveToFilesystemPath(realProjectPath);
        Assert.Equal(resolvedRealTarget, resolvedViaSymlink);

        var prefixViaResolvedSymlink = BackchannelConstants.ComputeSocketPrefix(resolvedViaSymlink, homeDirectory);
        var prefixForRealTarget = BackchannelConstants.ComputeSocketPrefix(resolvedRealTarget, homeDirectory);
        Assert.Equal(prefixForRealTarget, prefixViaResolvedSymlink);

        // The raw (unresolved) symlinked path hashes to a different prefix — exactly the mismatch that
        // caused detached `aspire start` to wait on a hash the AppHost never used and time out.
        var prefixViaRawSymlink = BackchannelConstants.ComputeSocketPrefix(projectFileViaSymlink, homeDirectory);
        Assert.NotEqual(prefixForRealTarget, prefixViaRawSymlink);
    }

    [Fact]
    public void ComputeLegacyHashes_IncludesDriveLetterOnlyHashSharedAcrossCasings()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "Drive-letter legacy fallback behavior only applies on Windows.");

        var upperDrivePath = @"C:\Path\To\MyApp.AppHost.csproj";
        var lowerDrivePath = @"c:\Path\To\MyApp.AppHost.csproj";

        var upperHashes = BackchannelConstants.ComputeLegacyHashes(upperDrivePath);
        var lowerHashes = BackchannelConstants.ComputeLegacyHashes(lowerDrivePath);

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
        var legacyHash = BackchannelConstants.ComputeLegacyHash(appHostPath);

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

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var upperPath = @"C:\Foo\Bar\App.AppHost.csproj";
        var mixedPath = @"c:\foo\BAR\app.apphost.CSPROJ";

        var appHostId = BackchannelConstants.ComputeAppHostId(upperPath);
        var socket = Path.Combine(backchannelsDir, $"{appHostId}a1b2C3d4.12345");
        File.WriteAllText(socket, "");

        var found = BackchannelConstants.FindMatchingSockets(mixedPath, workspace.WorkspaceRoot.FullName);
        Assert.Single(found);
        Assert.Contains(socket, found);
    }

    [Fact]
    public void FindMatchingSockets_FindsSocketsCreatedWithDifferentDriveLetterCasing()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(),
            "Drive letter normalization only applies on Windows.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        // Simulate the real-world mismatch: FileInfo.FullName yields an uppercase drive letter
        // (e.g. "C:\...") while MSBuild metadata may yield a lowercase one (e.g. "c:\...").
        // Only the drive letter casing differs; the rest of the path is identical.
        var upperDrivePath = @"C:\Development\MyApp\MyApp.AppHost.csproj";
        var lowerDrivePath = @"c:\Development\MyApp\MyApp.AppHost.csproj";

        // Both should produce the same AppHost ID after drive-letter normalization.
        var upperPrefix = BackchannelConstants.ComputeSocketPrefix(upperDrivePath, workspace.WorkspaceRoot.FullName);
        var lowerPrefix = BackchannelConstants.ComputeSocketPrefix(lowerDrivePath, workspace.WorkspaceRoot.FullName);
        Assert.Equal(upperPrefix, lowerPrefix);

        var appHostId = Path.GetFileName(upperPrefix);

        var socket = Path.Combine(backchannelsDir, $"{appHostId}a1b2C3d4.12345");
        File.WriteAllText(socket, "");

        // Both path variants should find the socket
        var fromUpper = BackchannelConstants.FindMatchingSockets(upperDrivePath, workspace.WorkspaceRoot.FullName);
        var fromLower = BackchannelConstants.FindMatchingSockets(lowerDrivePath, workspace.WorkspaceRoot.FullName);

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

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var backchannelsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cli", "backchannels");
        Directory.CreateDirectory(backchannelsDir);

        // A path with a lowercase drive letter produces a legacy hash that differs from the
        // normalized hash (which has an uppercase drive letter).
        var appHostPath = @"c:\Development\MyApp\MyApp.AppHost.csproj";
        var legacyHash = BackchannelConstants.ComputeLegacyHash(appHostPath);
        Assert.NotNull(legacyHash);

        // Create a socket using the legacy (pre-normalization) hash, as an older AppHost would
        var legacySocket = Path.Combine(backchannelsDir, $"auxi.sock.{legacyHash}.a1b2c3d4e5f6.99999");
        File.WriteAllText(legacySocket, "");

        var currentHash = BackchannelConstants.ComputeLegacyHashes(appHostPath)[0];
        Assert.NotEqual(currentHash, legacyHash);

        // FindMatchingSockets should still find the legacy socket via fallback
        var found = BackchannelConstants.FindMatchingSockets(appHostPath, workspace.WorkspaceRoot.FullName);
        Assert.Single(found);
        Assert.Contains(legacySocket, found);
    }
}
