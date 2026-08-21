// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class SelectAppHostToolTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task SelectAppHostTool_WithSymlinkedPath_MatchesPhysicalAppHostPath()
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
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection(realAppHostPath, processId: 2);
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = new SelectAppHostTool(
            monitor,
            TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot));

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(CreateArguments(symlinkedAppHostPath)),
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
        Assert.Equal(Path.GetFullPath(symlinkedAppHostPath), monitor.SelectedAppHostPath);
    }

    [Fact]
    public async Task SelectAppHostTool_WithCaseVariant_FollowsCurrentVolumeBehavior()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot);
        var directory = workspace.WorkspaceRoot.CreateSubdirectory("CaseSensitiveAppHost");
        var actualAppHostPath = Path.Combine(directory.FullName, "CaseSensitive.AppHost.csproj");
        File.WriteAllText(actualAppHostPath, "<Project />");
        var selectedAppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "casesensitiveapphost", "casesensitive.apphost.csproj");
        var resolvedSelectedAppHostPath = Path.GetFullPath(selectedAppHostPath);
        var volumeResolvesCaseVariant = File.Exists(selectedAppHostPath);
        var connection = CreateConnection(actualAppHostPath, processId: 1);

        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);

        var tool = new SelectAppHostTool(monitor, executionContext);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments(selectedAppHostPath)), CancellationToken.None).DefaultTimeout();

        if (volumeResolvesCaseVariant)
        {
            Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
            Assert.Equal(resolvedSelectedAppHostPath, monitor.SelectedAppHostPath);
        }
        else
        {
            Assert.True(result.IsError is true, "Case-variant AppHost selection should fail on a case-sensitive volume.");
            Assert.Null(monitor.SelectedAppHostPath);

            var text = GetResultText(result);
            Assert.Contains($"No running AppHost found at path '{resolvedSelectedAppHostPath}'.", text);
            Assert.Contains(actualAppHostPath, text);
        }

        var selectedConnection = await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Same(connection, selectedConnection);
    }

    private static Dictionary<string, JsonElement> CreateArguments(string appHostPath)
    {
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["appHostPath"] = JsonDocument.Parse(JsonSerializer.Serialize(appHostPath)).RootElement
        };
    }

    private static TestAppHostAuxiliaryBackchannel CreateConnection(string appHostPath, int processId)
        => new()
        {
            Hash = $"hash-{processId}",
            SocketPath = $"socket-{processId}",
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = processId,
                CliProcessId = null
            },
            IsInScope = true
        };

    private static string GetResultText(CallToolResult result)
        => result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
}
