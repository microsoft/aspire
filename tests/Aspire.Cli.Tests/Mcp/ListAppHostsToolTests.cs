// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Mcp;

public class ListAppHostsToolTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ListAppHostsTool_ReturnsEmptyListWhenNoConnections()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var executionContext = CreateCliExecutionContext(workspace.WorkspaceRoot);

        var tool = new ListAppHostsTool(monitor, executionContext);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.Null(result.IsError);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);

        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);
        var text = textContent.Text;

        Assert.Contains("The following is a list of apphosts which are currently running", text);
        Assert.Contains($"App hosts within scope of working directory: {workspace.WorkspaceRoot.FullName}", text);
        Assert.Contains("App hosts outside scope of working directory:", text);
        Assert.Contains("[]", text); // Both lists should be empty
    }

    [Fact]
    public async Task ListAppHostsTool_ReturnsInScopeAppHosts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var executionContext = CreateCliExecutionContext(workspace.WorkspaceRoot);

        // Create a mock connection that is in scope
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost");
        var appHostInfo = new AppHostInformation
        {
            AppHostPath = appHostPath,
            ProcessId = 1234,
            CliProcessId = 5678
        };
        var connection = CreateAppHostConnection("hash1", "/tmp/socket1", appHostInfo, isInScope: true);
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListAppHostsTool(monitor, executionContext);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.Null(result.IsError);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);
        var text = textContent.Text;

        Assert.Contains("appHostPath", text);
        Assert.Contains("TestAppHost", text); // just test for part of path to avoid escaping issues.
        Assert.Contains("1234", text);
        Assert.Contains("5678", text);
    }

    [Fact]
    public async Task ListAppHostsTool_ReturnsOutOfScopeAppHosts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var executionContext = CreateCliExecutionContext(workspace.WorkspaceRoot);

        // Create a mock connection that is out of scope
        var appHostPath = "/other/path/TestAppHost";
        var appHostInfo = new AppHostInformation
        {
            AppHostPath = appHostPath,
            ProcessId = 9999,
            CliProcessId = null
        };
        var connection = CreateAppHostConnection("hash2", "/tmp/socket2", appHostInfo, isInScope: false);
        monitor.AddConnection("hash2", "socket.hash2", connection);

        var tool = new ListAppHostsTool(monitor, executionContext);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.Null(result.IsError);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);
        var text = textContent.Text;

        // Out of scope app hosts should appear in the second section
        Assert.Contains("App hosts outside scope of working directory:", text);
        Assert.Contains(appHostPath, text);
        Assert.Contains("9999", text);
    }

    [Fact]
    public async Task ListAppHostsTool_SeparatesInScopeAndOutOfScopeAppHosts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var executionContext = CreateCliExecutionContext(workspace.WorkspaceRoot);

        // Create in-scope connection
        var inScopeAppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "InScopeAppHost");
        var inScopeAppHostInfo = new AppHostInformation
        {
            AppHostPath = inScopeAppHostPath,
            ProcessId = 1111,
            CliProcessId = 2222
        };
        var inScopeConnection = CreateAppHostConnection("hash1", "/tmp/socket1", inScopeAppHostInfo, isInScope: true);
        monitor.AddConnection("hash1", "socket.hash1", inScopeConnection);

        // Create out-of-scope connection
        var outOfScopeAppHostPath = "/other/path/OutOfScopeAppHost";
        var outOfScopeAppHostInfo = new AppHostInformation
        {
            AppHostPath = outOfScopeAppHostPath,
            ProcessId = 3333,
            CliProcessId = 4444
        };
        var outOfScopeConnection = CreateAppHostConnection("hash2", "/tmp/socket2", outOfScopeAppHostInfo, isInScope: false);
        monitor.AddConnection("hash2", "socket.hash2", outOfScopeConnection);

        var tool = new ListAppHostsTool(monitor, executionContext);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.Null(result.IsError);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);
        var text = textContent.Text;

        // Both paths should be present in the output
        Assert.Contains("InScopeAppHost", text);
        Assert.Contains("OutOfScopeAppHost", text);
        Assert.Contains("1111", text);
        Assert.Contains("3333", text);
    }

    [Fact]
    public async Task ListAppHostsTool_CallsScanAsyncBeforeReturningResults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var executionContext = CreateCliExecutionContext(workspace.WorkspaceRoot);

        Assert.Equal(0, monitor.ScanCallCount);

        var tool = new ListAppHostsTool(monitor, executionContext);
        await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.Equal(1, monitor.ScanCallCount);

        // Call again to verify it scans each time
        await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.Equal(2, monitor.ScanCallCount);
    }

    private static CliExecutionContext CreateCliExecutionContext(DirectoryInfo workingDirectory)
    {
        var hivesDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "hives"));
        var cacheDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "cache"));
        return new CliExecutionContext(workingDirectory, hivesDirectory, cacheDirectory, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
    }

    private static AppHostAuxiliaryBackchannel CreateAppHostConnection(string hash, string socketPath, AppHostInformation appHostInfo, bool isInScope)
    {
        // Create a mock JsonRpc that won't be used
        var rpc = new JsonRpc(Stream.Null);
        return new AppHostAuxiliaryBackchannel(hash, socketPath, rpc, mcpInfo: null, appHostInfo, isInScope);
    }
}

