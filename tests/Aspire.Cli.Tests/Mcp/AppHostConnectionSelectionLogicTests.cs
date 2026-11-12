// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Aspire.Cli.Tests.Mcp;

public class AppHostConnectionSelectionLogicTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void AuxiliaryBackchannelMonitorContract_DoesNotExposeFailOpenSelectionShortcuts()
    {
        Assert.Equal(
            ["Connections", "SelectedAppHostPath"],
            typeof(IAuxiliaryBackchannelMonitor)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SelectedConnectionReturnsNullWhenNoConnections()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();

        Assert.Null(monitor.SelectedConnection);
    }

    [Fact]
    public void SelectedConnectionPrefersExplicitSelectionWhenAvailable()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var inScope = CreateConnection(appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1);
        var outOfScope = CreateConnection(appHostPath: "C:/other/AppHost2", isInScope: false, processId: 2);
        monitor.AddConnection(inScope.Hash, inScope.SocketPath, inScope);
        monitor.AddConnection(outOfScope.Hash, outOfScope.SocketPath, outOfScope);
        monitor.SelectedAppHostPath = "C:/other/AppHost2";

        Assert.Same(outOfScope, monitor.SelectedConnection);
    }

    [Fact]
    public void SelectedConnectionClearsExplicitSelectionWhenNoLongerAvailable()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var inScope = CreateConnection(appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1);
        monitor.AddConnection(inScope.Hash, inScope.SocketPath, inScope);
        monitor.SelectedAppHostPath = "C:/missing/AppHost";

        var selected = monitor.SelectedConnection;

        Assert.Same(inScope, selected);
        Assert.Null(monitor.SelectedAppHostPath);
    }

    [Fact]
    public void SelectedConnectionPrefersSingleInScopeConnectionWhenNoExplicitSelection()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var inScope = CreateConnection(appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1);
        var outOfScope = CreateConnection(appHostPath: "C:/other/AppHost2", isInScope: false, processId: 2);
        monitor.AddConnection(inScope.Hash, inScope.SocketPath, inScope);
        monitor.AddConnection(outOfScope.Hash, outOfScope.SocketPath, outOfScope);

        Assert.Same(inScope, monitor.SelectedConnection);
    }

    [Fact]
    public void SelectedConnectionDistinguishesCaseDistinctAppHosts()
    {
        var tempRoot = Directory.CreateTempSubdirectory("aspire-apphost-selection-casing-");
        try
        {
            var firstDirectory = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "AppHost"));
            var secondDirectoryPath = Path.Combine(tempRoot.FullName, "apphost");
            Assert.SkipWhen(Directory.Exists(secondDirectoryPath),
                "This test requires a case-sensitive filesystem.");

            var secondDirectory = Directory.CreateDirectory(secondDirectoryPath);
            var firstPath = Path.Combine(firstDirectory.FullName, "AppHost.csproj");
            var secondPath = Path.Combine(secondDirectory.FullName, "AppHost.csproj");
            File.WriteAllText(firstPath, "<Project />");
            File.WriteAllText(secondPath, "<Project />");
            var firstConnection = CreateConnection(firstPath, isInScope: true, processId: 1);
            var secondConnection = CreateConnection(secondPath, isInScope: true, processId: 2);
            var monitor = new TestAuxiliaryBackchannelMonitor();
            monitor.AddConnection(firstConnection.Hash, firstConnection.SocketPath, firstConnection);
            monitor.AddConnection(secondConnection.Hash, secondConnection.SocketPath, secondConnection);
            monitor.SelectedAppHostPath = secondPath;

            Assert.Same(secondConnection, monitor.SelectedConnection);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(@"c:\Repo\AppHost.csproj", @"C:\Repo\AppHost.csproj")]
    [InlineData("d:/Repo/AppHost.csproj", "D:/Repo/AppHost.csproj")]
    [InlineData(@"C:\repo\AppHost.csproj", @"C:\repo\AppHost.csproj")]
    [InlineData(@"\\?\c:\Repo\AppHost.csproj", @"\\?\C:\Repo\AppHost.csproj")]
    [InlineData(@"\\.\d:\Repo\AppHost.csproj", @"\\.\D:\Repo\AppHost.csproj")]
    [InlineData(@"\\server\share\Repo\AppHost.csproj", @"\\SERVER\SHARE\Repo\AppHost.csproj")]
    [InlineData(@"\\?\UNC\server\share\Repo\AppHost.csproj", @"\\?\UNC\SERVER\SHARE\Repo\AppHost.csproj")]
    [InlineData(@"\\.\UNC\server\share\Repo\AppHost.csproj", @"\\.\UNC\SERVER\SHARE\Repo\AppHost.csproj")]
    [InlineData(@"c:relative\AppHost.csproj", @"c:relative\AppHost.csproj")]
    [InlineData("/repo/AppHost.csproj", "/repo/AppHost.csproj")]
    public void NormalizeRootIdentity_OnlyCanonicalizesVolumeRoot(
        string path,
        string expected)
    {
        Assert.Equal(expected, AppHostPathComparer.NormalizeRootIdentity(path));
    }

    [Fact]
    public void PathsEqual_WithDriveRootCaseVariant_MatchesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows drive-root identity is only available on Windows.");
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("CaseSensitiveSegments");
        var appHostPath = Path.Combine(appHostDirectory.FullName, "RootCase.AppHost.csproj");
        File.WriteAllText(appHostPath, "<Project />");
        var driveCaseVariant = char.ToLowerInvariant(appHostPath[0]) + appHostPath[1..];

        Assert.True(AppHostPathComparer.PathsEqual(appHostPath, driveCaseVariant));
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_WithSymlinkedSelectedPath_MatchesPhysicalAppHostPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var realAppHostPath = Path.Combine(realDirectory.FullName, "Symlinked.AppHost.csproj");
        File.WriteAllText(realAppHostPath, "<Project />");
        var symlinkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");

        try
        {
            Directory.CreateSymbolicLink(symlinkDirectory, realDirectory.FullName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Cannot create a directory symlink in this environment: {ex.Message}");
        }

        var symlinkedAppHostPath = Path.Combine(symlinkDirectory, "Symlinked.AppHost.csproj");
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            SelectedAppHostPath = symlinkedAppHostPath
        };
        var connection = CreateConnection(realAppHostPath, isInScope: true, processId: 4);
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);

        var selected = await AppHostConnectionHelper.GetSelectedConnectionAsync(
            monitor,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Same(connection, selected);
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_ReturnsNullWhenNoConnections()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();

        var selected = await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Null(selected);
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_PrefersExplicitSelectionWhenAvailable()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();

        var inScope = CreateConnection(appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1);
        var outOfScope = CreateConnection(appHostPath: "C:/other/AppHost2", isInScope: false, processId: 2);

        monitor.AddConnection("hash1", "socket.hash1", inScope);
        monitor.AddConnection("hash2", "socket.hash2", outOfScope);
        monitor.SelectedAppHostPath = "C:/other/AppHost2";

        var selected = await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Same(outOfScope, selected);
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_WithCaseVariant_FollowsCurrentVolumeBehavior()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.WorkspaceRoot.CreateSubdirectory("CaseSensitiveAppHost");
        var actualAppHostPath = Path.Combine(directory.FullName, "CaseSensitive.AppHost.csproj");
        File.WriteAllText(actualAppHostPath, "<Project />");
        var selectedAppHostPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "casesensitiveapphost",
            "casesensitive.apphost.csproj");
        var volumeResolvesCaseVariant = File.Exists(selectedAppHostPath);
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            SelectedAppHostPath = selectedAppHostPath
        };
        var connection = CreateConnection(appHostPath: actualAppHostPath, isInScope: true, processId: 3);
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);

        if (volumeResolvesCaseVariant)
        {
            var selected = await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken);

            Assert.Same(connection, selected);
            Assert.Equal(selectedAppHostPath, monitor.SelectedAppHostPath);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
                await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken));
            Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
            Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
            Assert.Equal(selectedAppHostPath, monitor.SelectedAppHostPath);
        }
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_WithCachedUnrelatedConnection_ScansForExplicitSelection()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var selectedAppHostPath = "C:/repo/PinnedAppHost";
        var cachedUnrelated = CreateConnection(appHostPath: "C:/repo/UnrelatedAppHost", isInScope: true, processId: 1);
        var selectedConnection = CreateConnection(appHostPath: selectedAppHostPath, isInScope: false, processId: 2);

        monitor.AddConnection(cachedUnrelated.Hash, cachedUnrelated.SocketPath, cachedUnrelated);
        monitor.SelectedAppHostPath = selectedAppHostPath;
        monitor.ScanAsyncCallback = _ =>
        {
            monitor.AddConnection(selectedConnection.Hash, selectedConnection.SocketPath, selectedConnection);
            return Task.CompletedTask;
        };

        var selected = await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Same(selectedConnection, selected);
        Assert.Equal(1, monitor.ScanCallCount);
        Assert.Equal(selectedAppHostPath, monitor.SelectedAppHostPath);
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_WithMissingSelectedAppHost_ThrowsWithoutFallingBack()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var selectedAppHostPath = "C:/missing/AppHost";
        var inScope = CreateConnection(appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 42);

        monitor.AddConnection("hash1", "socket.hash1", inScope);
        monitor.SelectedAppHostPath = selectedAppHostPath;

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal(selectedAppHostPath, monitor.SelectedAppHostPath);
        Assert.Equal(
            "The selected AppHost is not available. Start that AppHost and retry.",
            exception.Message);
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_WithMultipleInstancesAtSelectedPath_ThrowsWithoutChoosingOne()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            SelectedAppHostPath = "C:/repo/AppHost"
        };
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection("C:/repo/AppHost", isInScope: true, processId: 41));
        monitor.AddConnection("hash2", "socket.hash2", CreateConnection("C:/repo/AppHost", isInScope: true, processId: 42));

        var exception = await Assert.ThrowsAsync<McpProtocolException>(() =>
            AppHostConnectionHelper.GetSelectedConnectionAsync(
                monitor,
                NullLogger.Instance,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "Multiple running AppHost instances match the selected path. Stop the extra instance and retry.",
            exception.Message);
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_ThrowsBoundedErrorWhenOnlyOutOfScopeConnectionsExist()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var outOfScope = CreateConnection(appHostPath: "C:/other/AppHost2", isInScope: false, processId: 2);

        monitor.AddConnection("hash2", "socket.hash2", outOfScope);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(() =>
            AppHostConnectionHelper.GetSelectedConnectionAsync(
                monitor,
                NullLogger.Instance,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "Running Aspire AppHosts were found outside the MCP server's working directory scope. " +
            "Use 'list_apphosts' to discover available AppHosts, then 'select_apphost' to choose one.",
            exception.Message);
    }

    [Fact]
    public async Task GetSelectedConnectionAsync_ThrowsWhenMultipleInScopeConnectionsExist()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();

        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1));
        monitor.AddConnection("hash2", "socket.hash2", CreateConnection(appHostPath: "C:/repo/AppHost2", isInScope: true, processId: 2));

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal(
            "Multiple Aspire AppHosts are running in the MCP server's working directory scope. " +
            "Use 'select_apphost' to choose the AppHost for this request.",
            exception.Message);
    }

    private static TestAppHostAuxiliaryBackchannel CreateConnection(string appHostPath, bool isInScope, int processId)
        => new()
        {
            Hash = $"hash-{processId}",
            SocketPath = $"/socket-{processId}.sock",
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = processId,
                CliProcessId = null
            },
            IsInScope = isInScope
        };
}
