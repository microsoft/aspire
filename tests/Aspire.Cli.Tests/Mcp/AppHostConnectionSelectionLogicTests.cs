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

        monitor.AddConnection("socket.hash1", inScope);
        monitor.AddConnection("socket.hash2", outOfScope);

        monitor.SelectedAppHostPath = "C:/other/AppHost2";

        Assert.Same(outOfScope, monitor.SelectedConnection);
    }

    [Fact]
    public void SelectedConnectionClearsExplicitSelectionWhenNoLongerAvailable()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();

        var inScope = CreateConnection("hash1", appHostPath: "C:/repo/AppHost1", isInScope: true, processId: 1);

        monitor.AddConnection("socket.hash1", inScope);
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

        monitor.AddConnection("socket.hash1", inScope);
        monitor.AddConnection("socket.hash2", outOfScope);

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

            var firstConnection = CreateConnection("hash1", firstPath, isInScope: true, processId: 1);
            var secondConnection = CreateConnection("hash2", secondPath, isInScope: true, processId: 2);
            var monitor = new TestAuxiliaryBackchannelMonitor();
            monitor.AddConnection("socket.hash1", firstConnection);
            monitor.AddConnection("socket.hash2", secondConnection);
            monitor.SelectedAppHostPath = secondPath;

            Assert.Same(secondConnection, monitor.SelectedConnection);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    private static AppHostAuxiliaryBackchannel CreateConnection(string hash, string appHostPath, bool isInScope, int processId)
    {
        _ = hash;
        var rpc = new JsonRpc(Stream.Null);

        return new AppHostAuxiliaryBackchannel(
            new TestAppHostSocket("/tmp/socket"),
            rpc,
            appHostInfo: new AppHostInformation { AppHostPath = appHostPath, ProcessId = processId, CliProcessId = null },
            isInScope);
    }
}
