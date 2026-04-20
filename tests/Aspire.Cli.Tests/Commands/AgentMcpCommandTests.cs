// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Mcp;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Threading.Channels;

namespace Aspire.Cli.Tests.Commands;

/// <summary>
/// In-process unit tests for AgentMcpCommand that test the MCP server functionality
/// without starting a new CLI process. The IO communication between the MCP server
/// and test client is abstracted using in-memory pipes via DI.
/// </summary>
public class AgentMcpCommandTests(ITestOutputHelper outputHelper)
{
    private async Task<McpTestContext> CreateMcpClientAsync(string? dashboardUrl = null)
    {
        var cts = new CancellationTokenSource();
        var workspace = TemporaryWorkspace.Create(outputHelper);
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXunit(outputHelper));
        var testTransport = new TestMcpServerTransport(loggerFactory);
        var backchannelMonitor = new TestAuxiliaryBackchannelMonitor();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.McpServerTransportFactory = _ => testTransport;
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
            options.AuxiliaryBackchannelMonitorFactory = _ => backchannelMonitor;
        });

        if (dashboardUrl is not null)
        {
            var handler = new MockHttpMessageHandler(request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("/api/telemetry/resources"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
                    };
                }
                if (url.Contains("/api/telemetry/"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":{},\"totalCount\":0,\"returnedCount\":0}", System.Text.Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            services.AddSingleton(handler);
            services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));
        }

        var serviceProvider = services.BuildServiceProvider();
        var agentMcpCommand = serviceProvider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = serviceProvider.GetRequiredService<RootCommand>();
        var commandLine = dashboardUrl is not null
            ? $"agent mcp --dashboard-url {dashboardUrl}"
            : "agent mcp";
        var parseResult = rootCommand.Parse(commandLine);

        var serverRunTask = Task.Run(async () =>
        {
            try
            {
                await agentMcpCommand.ExecuteCommandAsync(parseResult, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);

        var mcpClient = await testTransport.CreateClientAsync(loggerFactory, cts.Token);

        return new McpTestContext(mcpClient, cts, workspace, serverRunTask, testTransport, serviceProvider, loggerFactory)
        {
            BackchannelMonitor = backchannelMonitor
        };
    }

    [Fact]
    public async Task McpServer_ListTools_ReturnsExpectedTools()
    {
        await using var ctx = await CreateMcpClientAsync();

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        // Assert
        Assert.NotNull(tools);
        Assert.Collection(tools.OrderBy(t => t.Name),
            tool => AssertTool(KnownMcpTools.Doctor, tool),
            tool => AssertTool(KnownMcpTools.ExecuteResourceCommand, tool),
            tool => AssertTool(KnownMcpTools.GetDoc, tool),
            tool => AssertTool(KnownMcpTools.ListAppHosts, tool),
            tool => AssertTool(KnownMcpTools.ListConsoleLogs, tool),
            tool => AssertTool(KnownMcpTools.ListDocs, tool),
            tool => AssertTool(KnownMcpTools.ListIntegrations, tool),
            tool => AssertTool(KnownMcpTools.ListResources, tool),
            tool => AssertTool(KnownMcpTools.ListStructuredLogs, tool),
            tool => AssertTool(KnownMcpTools.ListTraceStructuredLogs, tool),
            tool => AssertTool(KnownMcpTools.ListTraces, tool),
            tool => AssertTool(KnownMcpTools.RefreshTools, tool),
            tool => AssertTool(KnownMcpTools.SearchDocs, tool),
            tool => AssertTool(KnownMcpTools.SelectAppHost, tool));

        static void AssertTool(string expectedName, McpClientTool tool)
        {
            Assert.Equal(expectedName, tool.Name);
            Assert.False(string.IsNullOrEmpty(tool.Description), $"Tool '{tool.Name}' should have a description");
            Assert.NotEqual(default, tool.JsonSchema);
        }
    }

    [Fact]
    public async Task McpServer_ListTools_IncludesResourceMcpTools()
    {
        await using var ctx = await CreateMcpClientAsync();

        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 12345
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "test-resource-abcd1234",
                    DisplayName = "test-resource",
                    ResourceType = "Container",
                    State = "Running",
                    McpServer = new ResourceSnapshotMcpServer
                    {
                        EndpointUrl = "http://localhost:8080/mcp",
                        Tools =
                        [
                            new Tool
                            {
                                Name = "resource_tool_one",
                                Description = "A test tool from the resource"
                            },
                            new Tool
                            {
                                Name = "resource_tool_two",
                                Description = "Another test tool from the resource"
                            }
                        ]
                    }
                }
            ]
        };

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);

        await ctx.Client.CallToolAsync(KnownMcpTools.RefreshTools, cancellationToken: ctx.Cts.Token).DefaultTimeout();

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        // Assert - Verify resource tools are included
        Assert.NotNull(tools);

        // The resource tools should be exposed with a prefixed name using the DisplayName (app-model name):
        // DisplayName "test-resource" becomes "test_resource" (dashes replaced with underscores)
        var resourceToolOne = tools.FirstOrDefault(t => t.Name == "test_resource_resource_tool_one");
        var resourceToolTwo = tools.FirstOrDefault(t => t.Name == "test_resource_resource_tool_two");

        Assert.NotNull(resourceToolOne);
        Assert.NotNull(resourceToolTwo);

        Assert.Equal("A test tool from the resource", resourceToolOne.Description);
        Assert.Equal("Another test tool from the resource", resourceToolTwo.Description);
    }

    [Fact]
    public async Task McpServer_CallTool_ResourceMcpTool_ReturnsResult()
    {
        await using var ctx = await CreateMcpClientAsync();

        var expectedToolResult = "Tool executed successfully with custom data";
        string? callResourceName = null;
        string? callToolName = null;

        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 12345
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "my-resource-abcd1234",
                    DisplayName = "my-resource",
                    ResourceType = "Container",
                    State = "Running",
                    McpServer = new ResourceSnapshotMcpServer
                    {
                        EndpointUrl = "http://localhost:8080/mcp",
                        Tools =
                        [
                            new Tool
                            {
                                Name = "do_something",
                                Description = "Does something useful"
                            }
                        ]
                    }
                }
            ],
            // Configure the handler to capture the arguments and return a specific result
            CallResourceMcpToolHandler = (resourceName, toolName, arguments, ct) =>
            {
                callResourceName = resourceName;
                callToolName = toolName;
                return Task.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = expectedToolResult }]
                });
            }
        };

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);

        await ctx.Client.CallToolAsync(KnownMcpTools.RefreshTools, cancellationToken: ctx.Cts.Token).DefaultTimeout();

        var result = await ctx.Client.CallToolAsync(
            "my_resource_do_something",
            cancellationToken: ctx.Cts.Token).DefaultTimeout();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
        Assert.NotNull(result.Content);
        Assert.NotEmpty(result.Content);

        var textContent = result.Content[0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Equal(expectedToolResult, textContent.Text);

        // Verify the handler was called with the correct resource and tool names
        Assert.Equal("my-resource", callResourceName);
        Assert.Equal("do_something", callToolName);
    }

    [Fact]
    public async Task McpServer_CallTool_ResourceMcpTool_UsesDisplayNameForRouting()
    {
        await using var ctx = await CreateMcpClientAsync();

        var expectedToolResult = "List schemas completed";
        string? callResourceName = null;
        string? callToolName = null;

        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 12345
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "db1-mcp-ypnvhwvw",
                    DisplayName = "db1-mcp",
                    ResourceType = "Container",
                    State = "Running",
                    McpServer = new ResourceSnapshotMcpServer
                    {
                        EndpointUrl = "http://localhost:8080/mcp",
                        Tools =
                        [
                            new Tool
                            {
                                Name = "list_schemas",
                                Description = "Lists database schemas"
                            }
                        ]
                    }
                }
            ],
            CallResourceMcpToolHandler = (resourceName, toolName, arguments, ct) =>
            {
                callResourceName = resourceName;
                callToolName = toolName;
                return Task.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = expectedToolResult }]
                });
            }
        };

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);
        await ctx.Client.CallToolAsync(KnownMcpTools.RefreshTools, cancellationToken: ctx.Cts.Token).DefaultTimeout();

        var result = await ctx.Client.CallToolAsync("db1_mcp_list_schemas", cancellationToken: ctx.Cts.Token).DefaultTimeout();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
        Assert.Equal("db1-mcp", callResourceName);
        Assert.Equal("list_schemas", callToolName);
    }

    [Fact]
    public async Task McpServer_CallTool_ListAppHosts_ReturnsResult()
    {
        await using var ctx = await CreateMcpClientAsync();

        var result = await ctx.Client.CallToolAsync(
            KnownMcpTools.ListAppHosts,
            cancellationToken: ctx.Cts.Token).DefaultTimeout();

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.IsError);
        Assert.NotNull(result.Content);
        Assert.NotEmpty(result.Content);

        var textContent = result.Content[0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("App hosts", textContent.Text);
    }

    [Fact]
    public async Task McpServer_CallTool_RefreshTools_ReturnsResult()
    {
        await using var ctx = await CreateMcpClientAsync();

        var notificationChannel = Channel.CreateUnbounded<JsonRpcNotification>();
        await using var notificationHandler = ctx.Client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (notification, cancellationToken) =>
            {
                notificationChannel.Writer.TryWrite(notification);
                return default;
            });

        var result = await ctx.Client.CallToolAsync(
            KnownMcpTools.RefreshTools,
            cancellationToken: ctx.Cts.Token).DefaultTimeout();

        // Assert - Verify result
        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
        Assert.NotNull(result.Content);
        Assert.NotEmpty(result.Content);

        var textContent = result.Content[0] as TextContentBlock;
        Assert.NotNull(textContent);

        // Verify the text content indicates refresh success (resource tool count is 0 in this test, so total = known tools)
        var expectedToolCount = KnownMcpTools.All.Count;
        Assert.Equal($"Tools refreshed: {expectedToolCount} tools available", textContent.Text);

        var notification = await notificationChannel.Reader.ReadAsync(ctx.Cts.Token).AsTask().DefaultTimeout();
        Assert.NotNull(notification);
        Assert.Equal(NotificationMethods.ToolListChangedNotification, notification.Method);
    }

    [Fact]
    public async Task McpServer_ListTools_DoesNotSendToolsListChangedNotification()
    {
        await using var ctx = await CreateMcpClientAsync();

        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 12345
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "db-mcp-abcd1234",
                    DisplayName = "db-mcp",
                    ResourceType = "Container",
                    State = "Running",
                    McpServer = new ResourceSnapshotMcpServer
                    {
                        EndpointUrl = "http://localhost:8080/mcp",
                        Tools =
                        [
                            new Tool
                            {
                                Name = "query_database",
                                Description = "Query a database"
                            }
                        ]
                    }
                }
            ]
        };

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);

        var notificationCount = 0;
        await using var notificationHandler = ctx.Client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (notification, cancellationToken) =>
            {
                Interlocked.Increment(ref notificationCount);
                return default;
            });

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        // Assert - tools should include the resource tool
        Assert.NotNull(tools);
        var dbMcpTool = tools.FirstOrDefault(t => t.Name == "db_mcp_query_database");
        Assert.NotNull(dbMcpTool);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var notificationChannel = Channel.CreateUnbounded<JsonRpcNotification>();
        await using var channelHandler = ctx.Client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (notification, _) =>
            {
                notificationChannel.Writer.TryWrite(notification);
                return default;
            });

        var received = false;
        try
        {
            await notificationChannel.Reader.ReadAsync(timeoutCts.Token);
            received = true;
        }
        catch (OperationCanceledException)
        {
            // Expected — no notification arrived within the timeout
        }

        Assert.False(received, "tools/list_changed notification should not be sent during tools/list handling");
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task McpServer_ListTools_CachesResourceToolMap_WhenConnectionUnchanged()
    {
        await using var ctx = await CreateMcpClientAsync();

        var getResourceSnapshotsCallCount = 0;
        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 12345
            },
            GetResourceSnapshotsHandler = (ct) =>
            {
                Interlocked.Increment(ref getResourceSnapshotsCallCount);
                return Task.FromResult(new List<ResourceSnapshot>
                {
                    new ResourceSnapshot
                    {
                        Name = "db-mcp-xyz",
                        DisplayName = "db-mcp",
                        ResourceType = "Container",
                        State = "Running",
                        McpServer = new ResourceSnapshotMcpServer
                        {
                            EndpointUrl = "http://localhost:8080/mcp",
                            Tools =
                            [
                                new Tool
                                {
                                    Name = "query_db",
                                    Description = "Query the database"
                                }
                            ]
                        }
                    }
                });
            }
        };

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);

        var tools1 = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        var tools2 = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        // Assert - Both calls return the resource tool
        Assert.Contains(tools1, t => t.Name == "db_mcp_query_db");
        Assert.Contains(tools2, t => t.Name == "db_mcp_query_db");

        // The resource tool map should be cached after the first call,
        // so GetResourceSnapshotsAsync should only be called once (during the first refresh).
        // Before the fix, TryGetResourceToolMap always returned false due to
        // SelectedAppHostPath vs SelectedConnection path mismatch, causing every
        // ListTools call to trigger a full refresh.
        Assert.Equal(1, getResourceSnapshotsCallCount);
    }

    [Fact]
    public async Task McpServer_CallTool_UnknownTool_ReturnsError()
    {
        await using var ctx = await CreateMcpClientAsync();

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await ctx.Client.CallToolAsync(
                "nonexistent_tool_that_does_not_exist",
                cancellationToken: ctx.Cts.Token).DefaultTimeout());

        Assert.Equal(McpErrorCode.MethodNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task McpServer_DashboardOnlyMode_ListTools_ReturnsOnlyTelemetryTools()
    {
        await using var ctx = await CreateMcpClientAsync(dashboardUrl: "http://localhost:18888");

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.NotNull(tools);
        Assert.Equal(3, tools.Count);
        Assert.Collection(tools.OrderBy(t => t.Name),
            tool => Assert.Equal(KnownMcpTools.ListStructuredLogs, tool.Name),
            tool => Assert.Equal(KnownMcpTools.ListTraceStructuredLogs, tool.Name),
            tool => Assert.Equal(KnownMcpTools.ListTraces, tool.Name));
    }

    [Fact]
    public async Task McpServer_DashboardOnlyMode_CallNonTelemetryTool_ReturnsError()
    {
        await using var ctx = await CreateMcpClientAsync(dashboardUrl: "http://localhost:18888");

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await ctx.Client.CallToolAsync(
                KnownMcpTools.ListResources,
                cancellationToken: ctx.Cts.Token).DefaultTimeout());

        Assert.Equal(McpErrorCode.MethodNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task McpServer_WithInvalidDashboardUrl_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var serviceProvider = services.BuildServiceProvider();

        var agentMcpCommand = serviceProvider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = serviceProvider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse("agent mcp --dashboard-url not-a-url");

        var exitCode = await agentMcpCommand.ExecuteCommandAsync(parseResult, CancellationToken.None).DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    private static string GetResultText(CallToolResult result)
    {
        if (result.Content?.FirstOrDefault() is TextContentBlock textContent)
        {
            return textContent.Text;
        }

        return string.Empty;
    }
}

internal sealed class McpTestContext(
    McpClient client,
    CancellationTokenSource cts,
    TemporaryWorkspace workspace,
    Task serverRunTask,
    TestMcpServerTransport testTransport,
    ServiceProvider serviceProvider,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    public McpClient Client => client;
    public CancellationTokenSource Cts => cts;
    public TemporaryWorkspace Workspace => workspace;
    public TestAuxiliaryBackchannelMonitor? BackchannelMonitor { get; init; }

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        await cts.CancelAsync();

        try
        {
            await serverRunTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }

        testTransport.Dispose();
        await serviceProvider.DisposeAsync();
        workspace.Dispose();
        loggerFactory.Dispose();
        cts.Dispose();
    }
}
