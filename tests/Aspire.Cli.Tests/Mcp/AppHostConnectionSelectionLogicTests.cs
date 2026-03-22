// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Mcp;

public class AppHostConnectionSelectionLogicTests
{
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

        var inScope = CreateConnection("hash1", appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1);
        var outOfScope = CreateConnection("hash2", appHostPath: "C:/other/AppHost2", isInScope: false, processId: 2);

        monitor.AddConnection("hash1", "socket.hash1", inScope);
        monitor.AddConnection("hash2", "socket.hash2", outOfScope);

        monitor.SelectedAppHostPath = "C:/other/AppHost2";

        Assert.Same(outOfScope, monitor.SelectedConnection);
    }

    [Fact]
    public void SelectedConnectionClearsExplicitSelectionWhenNoLongerAvailable()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();

        var inScope = CreateConnection("hash1", appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1);

        monitor.AddConnection("hash1", "socket.hash1", inScope);
        monitor.SelectedAppHostPath = "C:/missing/AppHost";

        var selected = monitor.SelectedConnection;

        Assert.Same(inScope, selected);
        Assert.Null(monitor.SelectedAppHostPath);
    }

    [Fact]
    public void SelectedConnectionPrefersSingleInScopeConnectionWhenNoExplicitSelection()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();

        var inScope = CreateConnection("hash1", appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1);
        var outOfScope = CreateConnection("hash2", appHostPath: "C:/other/AppHost2", isInScope: false, processId: 2);

        monitor.AddConnection("hash1", "socket.hash1", inScope);
        monitor.AddConnection("hash2", "socket.hash2", outOfScope);

        Assert.Same(inScope, monitor.SelectedConnection);
    }

    private static AppHostAuxiliaryBackchannel CreateConnection(string hash, string appHostPath, bool isInScope, int processId)
    {
        var rpc = new JsonRpc(Stream.Null);

        return new AppHostAuxiliaryBackchannel(
            hash,
            socketPath: "/tmp/socket",
            rpc,
            mcpInfo: null,
            appHostInfo: new AppHostInformation { AppHostPath = appHostPath, ProcessId = processId, CliProcessId = null },
            isInScope);
    }
}

