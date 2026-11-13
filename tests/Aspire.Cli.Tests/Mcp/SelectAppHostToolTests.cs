// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class SelectAppHostToolTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallToolAsync_PreservesSuppliedPathInResponse(bool hasMatchingConnection)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Symlink path spelling test only runs on Unix-like platforms.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var appHostFile = new FileInfo(Path.Combine(realDirectory.FullName, "AppHost.csproj"));
        File.WriteAllText(appHostFile.FullName, "<Project />");

        var symlinkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");
        TestSymlinkHelper.TryCreateSymlink(symlinkDirectory, realDirectory.FullName);
        var suppliedPath = Path.Combine("link", appHostFile.Name);
        var displayPath = Path.GetFullPath(Path.Combine(workspace.WorkspaceRoot.FullName, suppliedPath));
        var canonicalPath = PathNormalizer.ResolveToFilesystemPath(displayPath);
        Assert.NotEqual(displayPath, canonicalPath);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        if (hasMatchingConnection)
        {
            var connection = CreateConnection(appHostFile.FullName, processId: Environment.ProcessId);
            monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        }

        var tool = new SelectAppHostTool(
            monitor,
            TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot));
        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(CreateArguments(suppliedPath)),
            TestContext.Current.CancellationToken);

        if (hasMatchingConnection)
        {
            Assert.Null(result.IsError);
            Assert.Equal($"Selected AppHost: {displayPath}", GetResultText(result));
            Assert.Equal(canonicalPath, monitor.SelectedAppHostPath);
        }
        else
        {
            Assert.True(result.IsError);
            AssertPathFreeSelectionError(
                result,
                "No running AppHost matched 'appHostPath'. No AppHosts are currently running.",
                displayPath,
                canonicalPath);
            Assert.Null(monitor.SelectedAppHostPath);
        }
    }

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
        Assert.Equal(PathNormalizer.ResolveToFilesystemPath(realAppHostPath), monitor.SelectedAppHostPath);
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
        var volumeResolvesCaseVariant = File.Exists(selectedAppHostPath);
        var connection = CreateConnection(actualAppHostPath, processId: 1);

        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);

        var tool = new SelectAppHostTool(monitor, executionContext);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments(selectedAppHostPath)), CancellationToken.None).DefaultTimeout();

        if (volumeResolvesCaseVariant)
        {
            Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
            Assert.Equal(PathNormalizer.ResolveToFilesystemPath(actualAppHostPath), monitor.SelectedAppHostPath);
        }
        else
        {
            Assert.True(result.IsError is true, "Case-variant AppHost selection should fail on a case-sensitive volume.");
            Assert.Null(monitor.SelectedAppHostPath);

            AssertPathFreeSelectionError(
                result,
                "No running AppHost matched 'appHostPath'. Other AppHosts are currently running.",
                selectedAppHostPath,
                actualAppHostPath);
        }

        var selectedConnection = await AppHostConnectionHelper.GetSelectedConnectionAsync(monitor, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Same(connection, selectedConnection);
    }

    [Fact]
    public async Task SelectAppHostTool_MissingPathDoesNotExposeRequestedOrAvailableAbsolutePaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstDirectory = workspace.WorkspaceRoot.CreateSubdirectory("FirstAppHost");
        var secondDirectory = workspace.WorkspaceRoot.CreateSubdirectory("SecondAppHost");
        var firstAppHostPath = Path.Combine(firstDirectory.FullName, "First.AppHost.csproj");
        var secondAppHostPath = Path.Combine(secondDirectory.FullName, "Second.AppHost.csproj");
        var requestedAppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "Missing", "Missing.AppHost.csproj");
        File.WriteAllText(firstAppHostPath, "<Project />");
        File.WriteAllText(secondAppHostPath, "<Project />");
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var firstConnection = CreateConnection(firstAppHostPath, processId: 1);
        var secondConnection = CreateConnection(secondAppHostPath, processId: 2);
        monitor.AddConnection(firstConnection.Hash, firstConnection.SocketPath, firstConnection);
        monitor.AddConnection(secondConnection.Hash, secondConnection.SocketPath, secondConnection);
        var tool = new SelectAppHostTool(
            monitor,
            TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot));

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(CreateArguments(requestedAppHostPath)),
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(result.IsError is true);
        Assert.Null(monitor.SelectedAppHostPath);
        AssertPathFreeSelectionError(
            result,
            "No running AppHost matched 'appHostPath'. Other AppHosts are currently running.",
            requestedAppHostPath,
            firstAppHostPath,
            secondAppHostPath);
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

    private static void AssertPathFreeSelectionError(
        CallToolResult result,
        string expectedMessage,
        params string[] absolutePaths)
    {
        var text = GetResultText(result);
        Assert.Equal(expectedMessage, text);
        Assert.All(absolutePaths, path => Assert.DoesNotContain(path, text, StringComparison.Ordinal));
    }
}
