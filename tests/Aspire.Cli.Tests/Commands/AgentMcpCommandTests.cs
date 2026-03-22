// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Mcp;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
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
public class AgentMcpCommandTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private TemporaryWorkspace _workspace = null!;
    private ServiceProvider _serviceProvider = null!;
    private TestMcpServerTransport _testTransport = null!;
    private McpClient _mcpClient = null!;
    private AgentMcpCommand _agentMcpCommand = null!;
    private Task _serverRunTask = null!;
    private CancellationTokenSource _cts = null!;
    private ILoggerFactory _loggerFactory = null!;
    private TestAuxiliaryBackchannelMonitor _backchannelMonitor = null!;

    public async ValueTask InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        _workspace = TemporaryWorkspace.Create(outputHelper);

        // Create the test transport with in-memory pipes
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXunit(outputHelper));
        _testTransport = new TestMcpServerTransport(_loggerFactory);

        // Create a backchannel monitor that we can configure for resource tool tests
        _backchannelMonitor = new TestAuxiliaryBackchannelMonitor();

        // Create services using CliTestHelper with custom MCP transport and test docs service
        var services = CliTestHelper.CreateServiceCollection(_workspace, outputHelper, options =>
        {
            // Override the MCP transport factory with our test transport (which implements IMcpTransportFactory)
            options.McpServerTransportFactory = _ => _testTransport;
            // Override the docs index service with a test implementation that doesn't make network calls
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
            // Override the backchannel monitor with our test implementation
            options.AuxiliaryBackchannelMonitorFactory = _ => _backchannelMonitor;
        });

        _serviceProvider = services.BuildServiceProvider();

        // Get the AgentMcpCommand from DI and start the server
        _agentMcpCommand = _serviceProvider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = _serviceProvider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse("agent mcp");

        // Start the MCP server in the background
        _serverRunTask = Task.Run(async () =>
        {
            try
            {
                await _agentMcpCommand.ExecuteCommandAsync(parseResult, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }, _cts.Token);

        // Create and connect the MCP client using the test transport's client side
        _mcpClient = await _testTransport.CreateClientAsync(_loggerFactory, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        await _cts.CancelAsync();

        try
        {
            if (_serverRunTask is not null)
            {
                await _serverRunTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (TimeoutException)
        {
            // Server didn't stop in time, but that's OK for tests
        }

        _testTransport?.Dispose();
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
        _workspace?.Dispose();
        _loggerFactory?.Dispose();
        _cts?.Dispose();
    }

    [Fact]
    public async Task McpServer_ListTools_ReturnsExpectedTools()
    {
        // Act
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: _cts.Token).DefaultTimeout();

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
        // Arrange - Create a mock backchannel with a resource that has MCP tools
        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
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

        // Register the mock backchannel
        _backchannelMonitor.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);

        // First call refresh_tools to discover the resource tools
        await _mcpClient.CallToolAsync(KnownMcpTools.RefreshTools, cancellationToken: _cts.Token).DefaultTimeout();

        // Act - List all tools
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: _cts.Token).DefaultTimeout();

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
        // Arrange - Create a mock backchannel with a resource that has MCP tools
        var expectedToolResult = "Tool executed successfully with custom data";
        string? callResourceName = null;
        string? callToolName = null;

        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
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

        // Register the mock backchannel
        _backchannelMonitor.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);

        // First call refresh_tools to discover the resource tools
        await _mcpClient.CallToolAsync(KnownMcpTools.RefreshTools, cancellationToken: _cts.Token).DefaultTimeout();

        // Act - Call the resource tool (name format: {resource_name}_{tool_name} with dashes replaced by underscores)
        var result = await _mcpClient.CallToolAsync(
            "my_resource_do_something",
            cancellationToken: _cts.Token).DefaultTimeout();

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
        // Arrange - Simulate resource snapshots that use a unique resource id and a logical display name.
        var expectedToolResult = "List schemas completed";
        string? callResourceName = null;
        string? callToolName = null;

        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
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

        _backchannelMonitor.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);
        await _mcpClient.CallToolAsync(KnownMcpTools.RefreshTools, cancellationToken: _cts.Token).DefaultTimeout();

        // Act
        var result = await _mcpClient.CallToolAsync("db1_mcp_list_schemas", cancellationToken: _cts.Token).DefaultTimeout();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
        Assert.Equal("db1-mcp", callResourceName);
        Assert.Equal("list_schemas", callToolName);
    }

    [Fact]
    public async Task McpServer_CallTool_ListAppHosts_ReturnsResult()
    {
        // Act
        var result = await _mcpClient.CallToolAsync(
            KnownMcpTools.ListAppHosts,
            cancellationToken: _cts.Token).DefaultTimeout();

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
        // Arrange - Set up a channel to receive the ToolListChanged notification
        var notificationChannel = Channel.CreateUnbounded<JsonRpcNotification>();
        await using var notificationHandler = _mcpClient.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (notification, cancellationToken) =>
            {
                notificationChannel.Writer.TryWrite(notification);
                return default;
            });

        // Act
        var result = await _mcpClient.CallToolAsync(
            KnownMcpTools.RefreshTools,
            cancellationToken: _cts.Token).DefaultTimeout();

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

        // Assert - Verify the ToolListChanged notification was received
        var notification = await notificationChannel.Reader.ReadAsync(_cts.Token).AsTask().DefaultTimeout();
        Assert.NotNull(notification);
        Assert.Equal(NotificationMethods.ToolListChangedNotification, notification.Method);
    }

    [Fact]
    public async Task McpServer_ListTools_DoesNotSendToolsListChangedNotification()
    {
        // Arrange - Create a mock backchannel with a resource that has MCP tools
        // This simulates the db-mcp scenario where resource tools become available
        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
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

        // Register the mock backchannel so resource tools will be discovered
        _backchannelMonitor.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);

        // Set up a channel to detect any tools/list_changed notifications
        var notificationCount = 0;
        await using var notificationHandler = _mcpClient.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (notification, cancellationToken) =>
            {
                Interlocked.Increment(ref notificationCount);
                return default;
            });

        // Act - Call ListTools which should discover the resource tools via refresh
        // but should NOT send a tools/list_changed notification (that would cause an infinite loop)
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: _cts.Token).DefaultTimeout();

        // Assert - tools should include the resource tool
        Assert.NotNull(tools);
        var dbMcpTool = tools.FirstOrDefault(t => t.Name == "db_mcp_query_database");
        Assert.NotNull(dbMcpTool);

        // Assert - no tools/list_changed notification should have been sent.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var notificationChannel = Channel.CreateUnbounded<JsonRpcNotification>();
        await using var channelHandler = _mcpClient.RegisterNotificationHandler(
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
        // Arrange - Create a mock backchannel and track how many times GetResourceSnapshotsAsync is called
        var getResourceSnapshotsCallCount = 0;
        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
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

        _backchannelMonitor.AddConnection(mockBackchannel.Hash, mockBackchannel.SocketPath, mockBackchannel);

        // Act - Call ListTools twice
        var tools1 = await _mcpClient.ListToolsAsync(cancellationToken: _cts.Token).DefaultTimeout();
        var tools2 = await _mcpClient.ListToolsAsync(cancellationToken: _cts.Token).DefaultTimeout();

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
        // Act & Assert - The MCP client throws McpProtocolException when the server returns an error
        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await _mcpClient.CallToolAsync(
                "nonexistent_tool_that_does_not_exist",
                cancellationToken: _cts.Token).DefaultTimeout());

        Assert.Equal(McpErrorCode.MethodNotFound, exception.ErrorCode);
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
