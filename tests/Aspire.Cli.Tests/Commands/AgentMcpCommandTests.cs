// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Mcp;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
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
    private async Task<McpTestContext> CreateMcpClientAsync(string? dashboardUrl = null, string? appHostPath = null)
    {
        var cts = new CancellationTokenSource();
        var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXunit(outputHelper));
        var testTransport = new TestMcpServerTransport(loggerFactory);
        var backchannelMonitor = new TestAuxiliaryBackchannelMonitor();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.McpServerTransportFactory = _ => testTransport;
            options.DocsIndexServiceFactory = _ => new TestDocsIndexService();
            options.AuxiliaryBackchannelMonitorFactory = _ => backchannelMonitor;
            if (appHostPath is not null)
            {
                options.ProjectLocatorFactory = _ => new TestProjectLocator();
            }
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

        // ServiceProvider lifetime is managed by McpTestContext.DisposeAsync, not this method.
        var serviceProvider = services.BuildServiceProvider();
        var agentMcpCommand = serviceProvider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = serviceProvider.GetRequiredService<RootCommand>();
        var commandLine = "agent mcp";
        if (dashboardUrl is not null)
        {
            commandLine += $" --dashboard-url {dashboardUrl}";
        }

        if (appHostPath is not null)
        {
            commandLine += $" --apphost \"{appHostPath}\"";
        }

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

        return new McpTestContext(mcpClient, cts, workspace, serverRunTask, testTransport, agentMcpCommand, serviceProvider, loggerFactory)
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
            tool => AssertTool(KnownMcpTools.SelectAppHost, tool),
            tool => AssertTool(KnownMcpTools.WaitForResources, tool));

        static void AssertTool(string expectedName, McpClientTool tool)
        {
            Assert.Equal(expectedName, tool.Name);
            Assert.False(string.IsNullOrEmpty(tool.Description), $"Tool '{tool.Name}' should have a description");
            Assert.NotEqual(default, tool.JsonSchema);
        }
    }

    [Fact]
    public async Task McpServer_ListTools_FixedToolsHaveAccurateAnnotations()
    {
        await using var ctx = await CreateMcpClientAsync();

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        AssertFixedToolAnnotations(tools);
    }

    [Fact]
    public async Task McpServer_ListTools_WithPinnedAppHost_FixedToolsKeepAccurateAnnotationsAfterRefresh()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "pinned-apphost-hash",
            SocketPath = "socket.pinned",
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 2002
            }
        };
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        await ctx.Client.CallToolAsync(KnownMcpTools.RefreshTools, cancellationToken: ctx.Cts.Token).DefaultTimeout();
        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        AssertFixedToolAnnotations(tools, KnownMcpTools.ListAppHosts, KnownMcpTools.SelectAppHost);
    }

    [Fact]
    public async Task McpServer_ListTools_WaitForResourcesHasExpectedSchema()
    {
        await using var ctx = await CreateMcpClientAsync();

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        var tool = Assert.Single(tools, tool => tool.Name == KnownMcpTools.WaitForResources);
        var schema = tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("required", out _));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        var properties = schema.GetProperty("properties");
        Assert.Equal(
            ["resourceNames", "targetState", "timeoutSeconds"],
            properties.EnumerateObject().Select(static property => property.Name));

        var resourceNames = properties.GetProperty("resourceNames");
        Assert.Equal("array", resourceNames.GetProperty("type").GetString());
        Assert.Equal(100, resourceNames.GetProperty("maxItems").GetInt32());
        Assert.Equal("string", resourceNames.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(256, resourceNames.GetProperty("items").GetProperty("maxLength").GetInt32());

        var targetState = properties.GetProperty("targetState");
        Assert.Equal("string", targetState.GetProperty("type").GetString());
        Assert.Equal("healthy", targetState.GetProperty("default").GetString());
        Assert.Equal(
            ["healthy", "up", "down"],
            targetState.GetProperty("enum").EnumerateArray().Select(static value => value.GetString()));

        var timeoutSeconds = properties.GetProperty("timeoutSeconds");
        Assert.Equal("integer", timeoutSeconds.GetProperty("type").GetString());
        Assert.Equal(120, timeoutSeconds.GetProperty("default").GetInt32());
        Assert.Equal(1, timeoutSeconds.GetProperty("minimum").GetInt32());
        Assert.Equal(3600, timeoutSeconds.GetProperty("maximum").GetInt32());
    }

    [Fact]
    public async Task McpServer_ListTools_IncludesResourceMcpTools()
    {
        await using var ctx = await CreateMcpClientAsync();

        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
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

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.SocketPath, mockBackchannel);

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
    public async Task McpServer_ListTools_DynamicToolAnnotationsPersistAfterRefresh()
    {
        await using var ctx = await CreateMcpClientAsync();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "annotated-apphost",
            SocketPath = "socket.annotated",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "AnnotatedAppHost", "AnnotatedAppHost.csproj"),
                ProcessId = 12345
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "annotated-resource-runtime",
                    DisplayName = "annotated-resource",
                    ResourceType = "Container",
                    State = "Running",
                    McpServer = new ResourceSnapshotMcpServer
                    {
                        EndpointUrl = "http://localhost:8080/mcp",
                        Tools =
                        [
                            new Tool
                            {
                                Name = "inspect",
                                Description = "Inspects the resource",
                                Annotations = new ToolAnnotations
                                {
                                    ReadOnlyHint = true,
                                    DestructiveHint = false
                                }
                            },
                            new Tool
                            {
                                Name = "reset",
                                Description = "Resets the resource",
                                Annotations = new ToolAnnotations
                                {
                                    ReadOnlyHint = false,
                                    DestructiveHint = true
                                }
                            }
                        ]
                    }
                }
            ]
        };
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        var initialTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        AssertDynamicToolAnnotations(initialTools);

        await ctx.Client.CallToolAsync(KnownMcpTools.RefreshTools, cancellationToken: ctx.Cts.Token).DefaultTimeout();
        var refreshedTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        AssertDynamicToolAnnotations(refreshedTools);

        static void AssertDynamicToolAnnotations(IList<McpClientTool> tools)
        {
            AssertToolAnnotations(
                Assert.Single(tools, static tool => tool.Name == "annotated_resource_inspect"),
                readOnly: true,
                destructive: false);
            AssertToolAnnotations(
                Assert.Single(tools, static tool => tool.Name == "annotated_resource_reset"),
                readOnly: false,
                destructive: true);
        }
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

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.SocketPath, mockBackchannel);

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
    public async Task McpServer_CallTool_ResourceMcpTool_UsesConnectionThatProducedToolMap()
    {
        await using var ctx = await CreateMcpClientAsync();
        var appHostAPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "AppHostA", "AppHostA.csproj");
        var appHostBPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "AppHostB", "AppHostB.csproj");
        var appHostAToolCalls = 0;
        var appHostBToolCalls = 0;
        var appHostA = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "apphost-a",
            socketPath: "socket.a",
            displayName: "resource-a",
            toolName: "tool_a",
            appHostPath: appHostAPath);
        var appHostB = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "apphost-b",
            socketPath: "socket.b",
            displayName: "resource-b",
            toolName: "tool_b",
            appHostPath: appHostBPath);
        appHostA.CallResourceMcpToolHandler = (_, _, _, _) =>
        {
            Interlocked.Increment(ref appHostAToolCalls);
            return Task.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "apphost-a" }]
            });
        };
        appHostB.CallResourceMcpToolHandler = (_, _, _, _) =>
        {
            Interlocked.Increment(ref appHostBToolCalls);
            return Task.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "apphost-b" }]
            });
        };
        appHostA.GetResourceSnapshotsHandler = _ =>
        {
            // Selection can change while the map is being built. Dispatch must remain bound to
            // the connection whose resource snapshot advertised the selected tool.
            ctx.BackchannelMonitor!.SelectedAppHostPath = appHostBPath;
            return Task.FromResult(appHostA.ResourceSnapshots);
        };
        ctx.BackchannelMonitor!.AddConnection(appHostA.Hash, appHostA.SocketPath, appHostA);
        ctx.BackchannelMonitor.AddConnection(appHostB.Hash, appHostB.SocketPath, appHostB);
        ctx.BackchannelMonitor.SelectedAppHostPath = appHostAPath;

        var result = await ctx.Client.CallToolAsync(
            "resource_a_tool_a",
            cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Equal("apphost-a", GetResultText(result));
        Assert.Equal(1, appHostAToolCalls);
        Assert.Equal(0, appHostBToolCalls);
        Assert.Equal(appHostBPath, ctx.BackchannelMonitor.SelectedAppHostPath);
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

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.SocketPath, mockBackchannel);
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
    public async Task McpServer_ListTools_WithPinnedAppHost_HidesAppHostSelectionTools()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "pinned-apphost-hash",
            SocketPath = "socket.pinned",
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 2002
            }
        };
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.NotNull(tools);
        Assert.Equal(
            [
                KnownMcpTools.Doctor,
                KnownMcpTools.ExecuteResourceCommand,
                KnownMcpTools.GetDoc,
                KnownMcpTools.ListConsoleLogs,
                KnownMcpTools.ListDocs,
                KnownMcpTools.ListIntegrations,
                KnownMcpTools.ListResources,
                KnownMcpTools.ListStructuredLogs,
                KnownMcpTools.ListTraceStructuredLogs,
                KnownMcpTools.ListTraces,
                KnownMcpTools.RefreshTools,
                KnownMcpTools.SearchDocs,
                KnownMcpTools.WaitForResources
            ],
            tools.Select(t => t.Name).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task McpServer_CallTool_HiddenAppHostSelectionTools_WithPinnedAppHost_ReturnUnavailableError()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);

        await AssertToolUnavailableAsync(KnownMcpTools.ListAppHosts);
        await AssertToolUnavailableAsync(KnownMcpTools.SelectAppHost);

        async Task AssertToolUnavailableAsync(string toolName)
        {
            var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
                await ctx.Client.CallToolAsync(
                    toolName,
                    cancellationToken: ctx.Cts.Token).DefaultTimeout());

            Assert.Equal(McpErrorCode.MethodNotFound, exception.ErrorCode);
            Assert.Equal(
                $"Request failed (remote): Tool '{toolName}' is not available because this MCP server is pinned to an AppHost. Start an unpinned MCP server to use AppHost selection tools.",
                exception.Message);
        }
    }

    [Fact]
    public async Task McpServer_WithPinnedAppHost_UsesOnlyPinnedConnectionForResourceToolDiscoveryAndRouting()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);

        var unrelatedSnapshotCalls = 0;
        var unrelatedToolCalls = 0;
        var pinnedSnapshotCalls = 0;
        var pinnedToolCalls = 0;

        var unrelatedConnection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "unrelated-apphost-hash",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "UnrelatedAppHost", "UnrelatedAppHost.csproj"),
                ProcessId = 1001
            },
            GetResourceSnapshotsHandler = _ =>
            {
                Interlocked.Increment(ref unrelatedSnapshotCalls);
                return Task.FromResult(new List<ResourceSnapshot>
                {
                    new ResourceSnapshot
                    {
                        Name = "unrelated-db-xyz",
                        DisplayName = "unrelated-db",
                        ResourceType = "Container",
                        State = "Running",
                        McpServer = new ResourceSnapshotMcpServer
                        {
                            EndpointUrl = "http://localhost:8081/mcp",
                            Tools =
                            [
                                new Tool
                                {
                                    Name = "drop_database",
                                    Description = "Drops the unrelated database"
                                }
                            ]
                        }
                    }
                });
            },
            CallResourceMcpToolHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref unrelatedToolCalls);
                return Task.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "unrelated" }]
                });
            }
        };

        var pinnedConnection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "pinned-apphost-hash",
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 2002
            },
            GetResourceSnapshotsHandler = _ =>
            {
                Interlocked.Increment(ref pinnedSnapshotCalls);
                return Task.FromResult(new List<ResourceSnapshot>
                {
                    new ResourceSnapshot
                    {
                        Name = "pinned-db-xyz",
                        DisplayName = "pinned-db",
                        ResourceType = "Container",
                        State = "Running",
                        McpServer = new ResourceSnapshotMcpServer
                        {
                            EndpointUrl = "http://localhost:8082/mcp",
                            Tools =
                            [
                                new Tool
                                {
                                    Name = "query_database",
                                    Description = "Queries the pinned database"
                                }
                            ]
                        }
                    }
                });
            },
            CallResourceMcpToolHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref pinnedToolCalls);
                return Task.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "pinned" }]
                });
            }
        };

        ctx.BackchannelMonitor!.AddConnection(unrelatedConnection.Hash, unrelatedConnection.SocketPath, unrelatedConnection);
        ctx.BackchannelMonitor.ScanAsyncCallback = _ =>
        {
            ctx.BackchannelMonitor.AddConnection(pinnedConnection.Hash, pinnedConnection.SocketPath, pinnedConnection);
            return Task.CompletedTask;
        };

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Equal(
            [
                KnownMcpTools.Doctor,
                KnownMcpTools.ExecuteResourceCommand,
                KnownMcpTools.GetDoc,
                KnownMcpTools.ListConsoleLogs,
                KnownMcpTools.ListDocs,
                KnownMcpTools.ListIntegrations,
                KnownMcpTools.ListResources,
                KnownMcpTools.ListStructuredLogs,
                KnownMcpTools.ListTraceStructuredLogs,
                KnownMcpTools.ListTraces,
                "pinned_db_query_database",
                KnownMcpTools.RefreshTools,
                KnownMcpTools.SearchDocs,
                KnownMcpTools.WaitForResources
            ],
            tools.Select(t => t.Name).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(0, unrelatedSnapshotCalls);
        Assert.Equal(1, pinnedSnapshotCalls);
        Assert.Equal(1, ctx.BackchannelMonitor.ScanCallCount);

        ctx.BackchannelMonitor.SelectedAppHostPath = unrelatedConnection.AppHostInfo!.AppHostPath;

        var result = await ctx.Client.CallToolAsync("pinned_db_query_database", cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
        Assert.Equal("pinned", GetResultText(result));
        Assert.Equal(appHostPath, ctx.BackchannelMonitor.SelectedAppHostPath);
        Assert.Equal(0, unrelatedToolCalls);
        Assert.Equal(1, pinnedToolCalls);
    }

    [Fact]
    public async Task McpServer_WithPinnedAppHost_ListWaitList_UsesSameConnectionAndPath()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);
        var unrelatedSnapshotCalls = 0;
        var unrelatedWaitCalls = 0;
        var unrelatedPath = Path.GetFullPath(Path.Combine("UnrelatedAppHost", "UnrelatedAppHost.csproj"));
        var unrelatedConnection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "unrelated-apphost-hash",
            SocketPath = "socket.unrelated",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = unrelatedPath,
                ProcessId = 1001
            },
            GetResourceSnapshotsHandler = _ =>
            {
                Interlocked.Increment(ref unrelatedSnapshotCalls);
                return Task.FromResult(new List<ResourceSnapshot>
                {
                    new()
                    {
                        Name = "unrelated",
                        ResourceType = "Container",
                        State = "Running"
                    }
                });
            },
            WaitForResourceHandler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref unrelatedWaitCalls);
                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running" });
            }
        };

        var currentState = "Starting";
        var pinnedWaitCalls = 0;
        var pinnedConnection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "pinned-apphost-hash",
            SocketPath = "socket.pinned",
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 2002
            },
            GetResourceSnapshotsHandler = _ => Task.FromResult(new List<ResourceSnapshot>
            {
                new()
                {
                    Name = "api",
                    DisplayName = "API",
                    ResourceType = "Project",
                    State = currentState,
                    HealthStatus = currentState == "Running" ? "Healthy" : null
                }
            }),
            WaitForResourceHandler = (resourceName, targetState, timeoutSeconds, _) =>
            {
                Assert.Equal("api", resourceName);
                Assert.Equal("up", targetState);
                Assert.Equal(15, timeoutSeconds);
                Interlocked.Increment(ref pinnedWaitCalls);
                currentState = "Running";
                return Task.FromResult(new WaitForResourceResponse
                {
                    Success = true,
                    State = currentState,
                    HealthStatus = "Healthy"
                });
            }
        };
        ctx.BackchannelMonitor!.AddConnection(
            unrelatedConnection.Hash,
            unrelatedConnection.SocketPath,
            unrelatedConnection);
        ctx.BackchannelMonitor.AddConnection(
            pinnedConnection.Hash,
            pinnedConnection.SocketPath,
            pinnedConnection);

        var firstList = await ctx.Client.CallToolAsync(
            KnownMcpTools.ListResources,
            cancellationToken: ctx.Cts.Token).DefaultTimeout();
        ctx.BackchannelMonitor.SelectedAppHostPath = unrelatedPath;

        var wait = await ctx.Client.CallToolAsync(
            KnownMcpTools.WaitForResources,
            new Dictionary<string, object?>
            {
                ["resourceNames"] = new[] { "api" },
                ["targetState"] = "up",
                ["timeoutSeconds"] = 15
            },
            cancellationToken: ctx.Cts.Token).DefaultTimeout();
        ctx.BackchannelMonitor.SelectedAppHostPath = unrelatedPath;

        var secondList = await ctx.Client.CallToolAsync(
            KnownMcpTools.ListResources,
            cancellationToken: ctx.Cts.Token).DefaultTimeout();

        using var firstListJson = GetMarkedJson(firstList, "# RESOURCE DATA");
        using var waitJson = GetMarkedJson(wait, "# WAIT RESULT");
        using var secondListJson = GetMarkedJson(secondList, "# RESOURCE DATA");
        Assert.False(waitJson.RootElement.TryGetProperty("app_host_path", out _));
        Assert.Equal(
            "Starting",
            firstListJson.RootElement[0].GetProperty("state").GetString());
        Assert.Equal("success", waitJson.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "Running",
            secondListJson.RootElement[0].GetProperty("state").GetString());
        Assert.Equal(3, pinnedConnection.GetResourceSnapshotsCallCount);
        Assert.Equal(1, pinnedWaitCalls);
        Assert.Equal(0, unrelatedSnapshotCalls);
        Assert.Equal(0, unrelatedWaitCalls);
        Assert.Equal(appHostPath, ctx.BackchannelMonitor.SelectedAppHostPath);
    }

    [Fact]
    public async Task McpServer_WithPinnedAppHost_CachesResourceToolsWhileConnectionRemainsAvailable()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);
        var snapshotCalls = 0;
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "pinned-apphost-hash",
            SocketPath = "socket.pinned",
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 2002
            },
            GetResourceSnapshotsHandler = _ =>
            {
                Interlocked.Increment(ref snapshotCalls);
                return Task.FromResult(new List<ResourceSnapshot>
                {
                    new()
                    {
                        Name = "pinned-db-xyz",
                        DisplayName = "pinned-db",
                        ResourceType = "Container",
                        State = "Running",
                        McpServer = new ResourceSnapshotMcpServer
                        {
                            EndpointUrl = "http://localhost:8082/mcp",
                            Tools =
                            [
                                new Tool
                                {
                                    Name = "query_database",
                                    Description = "Queries the pinned database"
                                }
                            ]
                        }
                    }
                });
            },
            CallResourceMcpToolHandler = (_, _, _, _) =>
                Task.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "pinned" }]
                })
        };
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        var firstTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        var secondTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        var result = await ctx.Client.CallToolAsync("pinned_db_query_database", cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Contains(firstTools, tool => tool.Name == "pinned_db_query_database");
        Assert.Contains(secondTools, tool => tool.Name == "pinned_db_query_database");
        Assert.Equal("pinned", GetResultText(result));
        Assert.Equal(1, snapshotCalls);

        ctx.BackchannelMonitor.RemoveConnection(connection.Hash, connection.SocketPath);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await ctx.Client.CallToolAsync(
                "pinned_db_query_database",
                cancellationToken: ctx.Cts.Token).DefaultTimeout());

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal(
            "Request failed (remote): The selected AppHost is not available. Start that AppHost and retry.",
            exception.Message);
        Assert.Equal(1, snapshotCalls);
    }

    [Fact]
    public async Task McpServer_ListTools_WithPinnedAppHostAndCanceledScan_PropagatesCancellation()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);

        ctx.BackchannelMonitor!.AddConnection(
            "unrelated-apphost-hash",
            "socket.unrelated",
            new TestAppHostAuxiliaryBackchannel
            {
                Hash = "unrelated-apphost-hash",
                SocketPath = "socket.unrelated",
                IsInScope = true,
                AppHostInfo = new AppHostInformation
                {
                    AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "UnrelatedAppHost", "UnrelatedAppHost.csproj"),
                    ProcessId = 1001
                }
            });

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cts.Token);
        ctx.BackchannelMonitor.ScanAsyncCallback = cancellationToken =>
        {
            requestCancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var handleListToolsAsync = typeof(AgentMcpCommand)
            .GetMethod("HandleListToolsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ((ValueTask<ListToolsResult>)handleListToolsAsync.Invoke(ctx.Command, [null, requestCancellation.Token])!).AsTask().DefaultTimeout());
    }

    [Fact]
    public async Task McpServer_ListTools_WithUnavailablePinnedAppHost_ReturnsBuiltInTools()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);
        ctx.BackchannelMonitor!.AddConnection(
            "unrelated-apphost-hash",
            "socket.unrelated",
            new TestAppHostAuxiliaryBackchannel
            {
                Hash = "unrelated-apphost-hash",
                SocketPath = "socket.unrelated",
                IsInScope = true,
                AppHostInfo = new AppHostInformation
                {
                    AppHostPath = Path.GetFullPath(Path.Combine("UnrelatedAppHost", "UnrelatedAppHost.csproj")),
                    ProcessId = 1001
                }
            });

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Equal(
            KnownMcpTools.All
                .Except([KnownMcpTools.ListAppHosts, KnownMcpTools.SelectAppHost])
                .OrderBy(static name => name, StringComparer.Ordinal),
            tools.Select(static tool => tool.Name).OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(1, ctx.BackchannelMonitor.ScanCallCount);
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
    public async Task McpServer_CallTool_RefreshTools_WithPinnedAppHost_ReportsVisibleToolCount()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "pinned-apphost-hash",
            SocketPath = "socket.pinned",
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 2002
            }
        };
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        var visibleTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        var result = await ctx.Client.CallToolAsync(
            KnownMcpTools.RefreshTools,
            cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Equal(KnownMcpTools.All.Count - 2, visibleTools.Count);
        Assert.Equal($"Tools refreshed: {visibleTools.Count} tools available", GetResultText(result));
    }

    [Fact]
    public async Task McpServer_CallTool_RefreshTools_WithUnavailablePinnedAppHost_PropagatesSelectionError()
    {
        var appHostPath = Path.GetFullPath(Path.Combine("PinnedAppHost", "PinnedAppHost.csproj"));
        await using var ctx = await CreateMcpClientAsync(appHostPath: appHostPath);
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "pinned-apphost-hash",
            SocketPath = "socket.pinned",
            IsInScope = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 2002
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "pinned-db-xyz",
                    DisplayName = "pinned-db",
                    ResourceType = "Container",
                    State = "Running",
                    McpServer = new ResourceSnapshotMcpServer
                    {
                        EndpointUrl = "http://localhost:8082/mcp",
                        Tools =
                        [
                            new Tool
                            {
                                Name = "query_database",
                                Description = "Queries the pinned database"
                            }
                        ]
                    }
                }
            ]
        };
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        Assert.Contains(tools, tool => tool.Name == "pinned_db_query_database");
        ctx.BackchannelMonitor.RemoveConnection(connection.Hash, connection.SocketPath);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await ctx.Client.CallToolAsync(
                KnownMcpTools.RefreshTools,
                cancellationToken: ctx.Cts.Token).DefaultTimeout());

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal(
            "Request failed (remote): The selected AppHost is not available. Start that AppHost and retry.",
            exception.Message);
    }

    [Fact]
    public async Task McpServer_CallTool_RefreshTools_WithTransientDiscoveryFailure_LeavesCliToolsAvailable()
    {
        await using var ctx = await CreateMcpClientAsync();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-apphost-hash",
            SocketPath = "socket.test",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 12345
            },
            GetResourceSnapshotsHandler = _ => throw new IOException("Transient resource discovery failure.")
        };
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        var result = await ctx.Client.CallToolAsync(
            KnownMcpTools.RefreshTools,
            cancellationToken: ctx.Cts.Token).DefaultTimeout();
        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Equal($"Tools refreshed: {KnownMcpTools.All.Count} tools available", GetResultText(result));
        Assert.Equal(
            KnownMcpTools.All.OrderBy(static name => name, StringComparer.Ordinal),
            tools.Select(tool => tool.Name).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task McpServer_ListTools_DoesNotSendToolsListChangedNotification()
    {
        await using var ctx = await CreateMcpClientAsync();

        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
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

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.SocketPath, mockBackchannel);

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
    public void ResourceToolEntry_ToProtocolTool_PreservesOutputSchema()
    {
        var source = CreateResourceToolContract();
        var projected = new ResourceToolEntry("resource", source).ToProtocolTool("resource_contract_tool");

        Assert.True(source.OutputSchema.HasValue);
        Assert.True(projected.OutputSchema.HasValue);
        Assert.True(JsonElement.DeepEquals(source.OutputSchema.Value, projected.OutputSchema.Value));
    }

    [Theory]
    [InlineData(ResourceToolContractMutation.Description)]
    [InlineData(ResourceToolContractMutation.InputSchema)]
    [InlineData(ResourceToolContractMutation.OutputSchema)]
    [InlineData(ResourceToolContractMutation.AnnotationTitle)]
    [InlineData(ResourceToolContractMutation.DestructiveHint)]
    [InlineData(ResourceToolContractMutation.IdempotentHint)]
    [InlineData(ResourceToolContractMutation.OpenWorldHint)]
    [InlineData(ResourceToolContractMutation.ReadOnlyHint)]
    public async Task McpServer_UnknownTool_SendsToolsListChangedWhenResourceToolContractChanges(
        ResourceToolContractMutation mutation)
    {
        await using var ctx = await CreateMcpClientAsync();
        var connection = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "contract-apphost",
            socketPath: "socket.contract",
            displayName: "contract-resource",
            toolName: "contract_tool");
        SetResourceToolContract(connection, CreateResourceToolContract());
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        var initialTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        Assert.Contains(initialTools, tool => tool.Name == "contract_resource_contract_tool");

        var notificationChannel = Channel.CreateUnbounded<JsonRpcNotification>();
        await using var notificationHandler = ctx.Client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (notification, _) =>
            {
                notificationChannel.Writer.TryWrite(notification);
                return default;
            });

        var changedTool = CreateResourceToolContract();
        ApplyResourceToolContractMutation(changedTool, mutation);
        SetResourceToolContract(connection, changedTool);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await ctx.Client.CallToolAsync(
                "force_resource_tool_contract_refresh",
                cancellationToken: ctx.Cts.Token).DefaultTimeout());
        Assert.Equal(McpErrorCode.MethodNotFound, exception.ErrorCode);

        var notification = await notificationChannel.Reader.ReadAsync(ctx.Cts.Token).AsTask().DefaultTimeout();
        Assert.Equal(NotificationMethods.ToolListChangedNotification, notification.Method);
    }

    [Fact]
    public async Task McpServer_UnknownTool_DoesNotSendToolsListChangedForEquivalentReorderedSchema()
    {
        await using var ctx = await CreateMcpClientAsync();
        var connection = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "contract-apphost",
            socketPath: "socket.contract",
            displayName: "contract-resource",
            toolName: "contract_tool");
        SetResourceToolContract(connection, CreateResourceToolContract());
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        var initialTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        Assert.Contains(initialTools, tool => tool.Name == "contract_resource_contract_tool");

        var notificationChannel = Channel.CreateUnbounded<JsonRpcNotification>();
        await using var notificationHandler = ctx.Client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (notification, _) =>
            {
                notificationChannel.Writer.TryWrite(notification);
                return default;
            });

        var equivalentTool = CreateResourceToolContract();
        equivalentTool.InputSchema = ParseJsonElement(
            """
            {
              "properties": {
                "second": { "type": "integer" },
                "first": { "type": "string" }
              },
              "type": "object"
            }
            """);
        SetResourceToolContract(connection, equivalentTool);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await ctx.Client.CallToolAsync(
                "force_equivalent_resource_tool_contract_refresh",
                cancellationToken: ctx.Cts.Token).DefaultTimeout());
        Assert.Equal(McpErrorCode.MethodNotFound, exception.ErrorCode);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var received = false;
        try
        {
            await notificationChannel.Reader.ReadAsync(timeoutCts.Token);
            received = true;
        }
        catch (OperationCanceledException)
        {
            // Expected because an equivalent JSON object does not change the listed tool contract.
        }

        Assert.False(received);
    }

    [Fact]
    public async Task McpServer_ListTools_CachesResourceToolMap_WhenConnectionUnchanged()
    {
        await using var ctx = await CreateMcpClientAsync();

        var getResourceSnapshotsCallCount = 0;
        var mockBackchannel = new TestAppHostAuxiliaryBackchannel
        {
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

        ctx.BackchannelMonitor!.AddConnection(mockBackchannel.SocketPath, mockBackchannel);

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
    public async Task McpServer_ListTools_DoesNotReuseCachedResourceTools_WhenConnectionBecomesOutOfScope()
    {
        await using var ctx = await CreateMcpClientAsync();
        var connection = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "apphost-a",
            socketPath: "socket.a",
            displayName: "cached-resource",
            toolName: "cached_tool");
        ctx.BackchannelMonitor!.AddConnection(connection.Hash, connection.SocketPath, connection);

        var initialTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        Assert.Contains(initialTools, tool => tool.Name == "cached_resource_cached_tool");

        connection.IsInScope = false;

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Equal(
            KnownMcpTools.All.OrderBy(static name => name, StringComparer.Ordinal),
            tools.Select(tool => tool.Name).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task McpServer_ListTools_DoesNotReuseCachedResourceTools_WhenSelectionBecomesAmbiguous()
    {
        await using var ctx = await CreateMcpClientAsync();
        var connectionA = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "apphost-a",
            socketPath: "socket.a",
            displayName: "resource-a",
            toolName: "tool_a");
        ctx.BackchannelMonitor!.AddConnection(connectionA.Hash, connectionA.SocketPath, connectionA);

        var initialTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        Assert.Contains(initialTools, tool => tool.Name == "resource_a_tool_a");

        var connectionB = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "apphost-b",
            socketPath: "socket.b",
            displayName: "resource-b",
            toolName: "tool_b");
        ctx.BackchannelMonitor.AddConnection(connectionB.Hash, connectionB.SocketPath, connectionB);

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Equal(
            KnownMcpTools.All.OrderBy(static name => name, StringComparer.Ordinal),
            tools.Select(static tool => tool.Name).OrderBy(static name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task McpServer_ListTools_RefreshesCachedResourceTools_WhenConnectionAtSamePathIsReplaced()
    {
        await using var ctx = await CreateMcpClientAsync();
        var appHostPath = Path.Combine(ctx.Workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj");
        var connectionA = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "apphost-a",
            socketPath: "socket.a",
            displayName: "resource-a",
            toolName: "tool_a",
            appHostPath: appHostPath);
        ctx.BackchannelMonitor!.AddConnection(connectionA.Hash, connectionA.SocketPath, connectionA);

        var initialTools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();
        Assert.Contains(initialTools, tool => tool.Name == "resource_a_tool_a");

        ctx.BackchannelMonitor.RemoveConnection(connectionA.Hash, connectionA.SocketPath);
        var connectionB = CreateResourceToolConnection(
            ctx.Workspace,
            hash: "apphost-b",
            socketPath: "socket.b",
            displayName: "resource-b",
            toolName: "tool_b",
            appHostPath: appHostPath);
        ctx.BackchannelMonitor.AddConnection(connectionB.Hash, connectionB.SocketPath, connectionB);

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.Equal(
            KnownMcpTools.All.Append("resource_b_tool_b").OrderBy(static name => name, StringComparer.Ordinal),
            tools.Select(tool => tool.Name).OrderBy(static name => name, StringComparer.Ordinal));
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
    public async Task McpServer_DashboardOnlyMode_ListTools_HasReadOnlyAnnotations()
    {
        await using var ctx = await CreateMcpClientAsync(dashboardUrl: "http://localhost:18888");

        var tools = await ctx.Client.ListToolsAsync(cancellationToken: ctx.Cts.Token).DefaultTimeout();

        Assert.All(tools, static tool => AssertToolAnnotations(tool, readOnly: true, destructive: false));
    }

    [Theory]
    [InlineData(KnownMcpTools.ListResources)]
    [InlineData(KnownMcpTools.WaitForResources)]
    public async Task McpServer_DashboardOnlyMode_CallNonTelemetryTool_ReturnsError(string toolName)
    {
        await using var ctx = await CreateMcpClientAsync(dashboardUrl: "http://localhost:18888");

        var exception = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await ctx.Client.CallToolAsync(
                toolName,
                cancellationToken: ctx.Cts.Token).DefaultTimeout());

        Assert.Equal(McpErrorCode.MethodNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task McpServer_WithInvalidDashboardUrl_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var serviceProvider = services.BuildServiceProvider();

        var agentMcpCommand = serviceProvider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = serviceProvider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse("agent mcp --dashboard-url not-a-url");

        var result = await agentMcpCommand.ExecuteCommandAsync(parseResult, CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
    }

    [Fact]
    public async Task McpServer_WithCredentialBearingInvalidDashboardUrl_DoesNotLeakValue()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var sink = new TestSink();
        var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.Replace(ServiceDescriptor.Singleton<ILogger<AgentMcpCommand>>(
            new TestLogger<AgentMcpCommand>(loggerFactory)));
        using var serviceProvider = services.BuildServiceProvider();

        var agentMcpCommand = serviceProvider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = serviceProvider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse(
            "agent mcp --dashboard-url " +
            "ftp://request-user:request-password@example.com/path?token=request-secret#request-fragment");

        var result = await agentMcpCommand.ExecuteCommandAsync(
            parseResult,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
        Assert.Equal(
            string.Format(
                CultureInfo.CurrentCulture,
                TelemetryCommandStrings.DashboardUrlInvalid,
                TelemetryCommandStrings.InvalidDashboardUrlDisplayValue),
            result.ErrorMessage);
        Assert.Collection(
            sink.Writes,
            write =>
            {
                Assert.Equal(
                    $"Invalid --dashboard-url: {TelemetryCommandStrings.InvalidDashboardUrlDisplayValue}",
                    write.Message);
                Assert.Null(write.Exception);
            });
    }

    [Theory]
    [InlineData("agent mcp --apphost Pinned.AppHost.csproj --dashboard-url http://localhost:18888")]
    [InlineData("agent mcp --apphost --dashboard-url http://localhost:18888")]
    [InlineData("agent mcp --project Pinned.AppHost.csproj --dashboard-url http://localhost:18888")]
    [InlineData("agent mcp --project --dashboard-url http://localhost:18888")]
    public async Task McpServer_WithAppHostAndDashboardUrl_ReturnsInvalidCommandBeforeResolutionOrServerStart(
        string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectResolutionCallCount = 0;
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (projectFile, _, _, _) =>
            {
                projectResolutionCallCount++;
                var selectedProjectFile = Assert.IsType<FileInfo>(projectFile);
                return Task.FromResult(new AppHostProjectSearchResult(selectedProjectFile, [selectedProjectFile]));
            }
        };
        using var testTransport = new TestMcpServerTransport
        {
            CreateTransportException = new InvalidOperationException("The server transport must not start.")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.McpServerTransportFactory = _ => testTransport;
        });
        using var serviceProvider = services.BuildServiceProvider();
        var command = serviceProvider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = serviceProvider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse(commandLine);

        var result = await command.ExecuteCommandAsync(
            parseResult,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
        Assert.Equal(TelemetryCommandStrings.DashboardUrlAndAppHostExclusive, result.ErrorMessage);
        Assert.Equal(0, projectResolutionCallCount);
        Assert.Equal(0, testTransport.CreateTransportCallCount);
    }

    [Fact]
    public async Task McpServer_WithPinnedAppHostFile_StoresResolvedProjectPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("PinnedAppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "PinnedAppHost.csproj"));
        await File.WriteAllTextAsync(appHostProjectFile.FullName, "Not a real apphost", TestContext.Current.CancellationToken);
        var serverStartException = new InvalidOperationException("Server transport should be reached for a valid AppHost.");
        using var testTransport = new TestMcpServerTransport
        {
            CreateTransportException = serverStartException
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.McpServerTransportFactory = _ => testTransport;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory();
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = provider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse($"agent mcp --apphost \"{appHostProjectFile.FullName}\"");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteCommandAsync(parseResult, TestContext.Current.CancellationToken)).DefaultTimeout();

        Assert.Same(serverStartException, exception);
        Assert.Equal(
            PathNormalizer.ResolveToFilesystemPath(PathNormalizer.ResolveSymlinks(appHostProjectFile.FullName)),
            monitor.SelectedAppHostPath);
    }

    [Fact]
    public async Task McpServer_WithPinnedAppHostDirectory_StoresResolvedProjectPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("PinnedAppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "PinnedAppHost.csproj"));
        await File.WriteAllTextAsync(appHostProjectFile.FullName, "Not a real apphost", TestContext.Current.CancellationToken);
        var serverStartException = new InvalidOperationException("Server transport should be reached for a valid AppHost.");
        using var testTransport = new TestMcpServerTransport
        {
            CreateTransportException = serverStartException
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.McpServerTransportFactory = _ => testTransport;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory();
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = provider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse($"agent mcp --apphost \"{appHostDirectory.FullName}\"");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteCommandAsync(parseResult, TestContext.Current.CancellationToken)).DefaultTimeout();

        Assert.Same(serverStartException, exception);
        Assert.Equal(
            PathNormalizer.ResolveToFilesystemPath(PathNormalizer.ResolveSymlinks(appHostProjectFile.FullName)),
            monitor.SelectedAppHostPath);
    }

    [Fact]
    public async Task McpServer_WithMissingPinnedAppHost_FailsBeforeTransportStarts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var missingPath = Path.Combine(workspace.WorkspaceRoot.FullName, "MissingAppHost");
        using var testTransport = new TestMcpServerTransport
        {
            CreateTransportException = new InvalidOperationException("The server transport must not start.")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.McpServerTransportFactory = _ => testTransport;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory();
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = provider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse($"agent mcp --apphost \"{missingPath}\"");

        var result = await command.ExecuteCommandAsync(
            parseResult,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
        Assert.Equal(InteractionServiceStrings.ProjectOptionDoesntExist, result.ErrorMessage);
    }

    [Fact]
    public async Task McpServer_WithAmbiguousPinnedAppHostDirectory_FailsBeforeTransportStarts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AmbiguousAppHosts");
        await File.WriteAllTextAsync(
            Path.Combine(appHostDirectory.FullName, "First.AppHost.csproj"),
            "Not a real apphost",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(appHostDirectory.FullName, "Second.AppHost.csproj"),
            "Not a real apphost",
            TestContext.Current.CancellationToken);
        using var testTransport = new TestMcpServerTransport
        {
            CreateTransportException = new InvalidOperationException("The server transport must not start.")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.McpServerTransportFactory = _ => testTransport;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory();
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentMcpCommand>();
        var rootCommand = provider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse($"agent mcp --apphost \"{appHostDirectory.FullName}\"");

        var result = await command.ExecuteCommandAsync(
            parseResult,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToFindProject, result.ExitCode);
        Assert.Equal(
            InteractionServiceStrings.ProjectOptionSpecifiedDirectoryContainsMultipleAppHosts,
            result.ErrorMessage);
    }

    private static TestAppHostAuxiliaryBackchannel CreateResourceToolConnection(
        TemporaryWorkspace workspace,
        string hash,
        string socketPath,
        string displayName,
        string toolName,
        string? appHostPath = null)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            Hash = hash,
            SocketPath = socketPath,
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath ?? Path.Combine(workspace.WorkspaceRoot.FullName, hash, $"{hash}.csproj"),
                ProcessId = 12345
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = $"{displayName}-runtime",
                    DisplayName = displayName,
                    ResourceType = "Container",
                    State = "Running",
                    McpServer = new ResourceSnapshotMcpServer
                    {
                        EndpointUrl = "http://localhost:8080/mcp",
                        Tools =
                        [
                            new Tool
                            {
                                Name = toolName,
                                Description = $"Runs {toolName}"
                            }
                        ]
                    }
                }
            ]
        };
    }

    private static Tool CreateResourceToolContract()
    {
        return new Tool
        {
            Name = "contract_tool",
            Description = "Initial description",
            InputSchema = ParseJsonElement(
                """
                {
                  "type": "object",
                  "properties": {
                    "first": { "type": "string" },
                    "second": { "type": "integer" }
                  }
                }
                """),
            OutputSchema = ParseJsonElement(
                """
                {
                  "type": "object",
                  "properties": {
                    "result": { "type": "string" }
                  }
                }
                """),
            Annotations = new ToolAnnotations
            {
                Title = "Initial title",
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
                ReadOnlyHint = true
            }
        };
    }

    private static void ApplyResourceToolContractMutation(Tool tool, ResourceToolContractMutation mutation)
    {
        switch (mutation)
        {
            case ResourceToolContractMutation.Description:
                tool.Description = "Changed description";
                break;
            case ResourceToolContractMutation.InputSchema:
                tool.InputSchema = ParseJsonElement(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "first": { "type": "string" },
                        "second": { "type": "integer" },
                        "third": { "type": "boolean" }
                      }
                    }
                    """);
                break;
            case ResourceToolContractMutation.OutputSchema:
                tool.OutputSchema = ParseJsonElement(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "result": { "type": "string" },
                        "metadata": { "type": "object" }
                      }
                    }
                    """);
                break;
            case ResourceToolContractMutation.AnnotationTitle:
                tool.Annotations!.Title = "Changed title";
                break;
            case ResourceToolContractMutation.DestructiveHint:
                tool.Annotations!.DestructiveHint = true;
                break;
            case ResourceToolContractMutation.IdempotentHint:
                tool.Annotations!.IdempotentHint = false;
                break;
            case ResourceToolContractMutation.OpenWorldHint:
                tool.Annotations!.OpenWorldHint = true;
                break;
            case ResourceToolContractMutation.ReadOnlyHint:
                tool.Annotations!.ReadOnlyHint = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static void SetResourceToolContract(TestAppHostAuxiliaryBackchannel connection, Tool tool)
    {
        connection.ResourceSnapshots =
        [
            new ResourceSnapshot
            {
                Name = "contract-resource-runtime",
                DisplayName = "contract-resource",
                ResourceType = "Container",
                State = "Running",
                McpServer = new ResourceSnapshotMcpServer
                {
                    EndpointUrl = "http://localhost:8080/mcp",
                    Tools = [tool]
                }
            }
        ];
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertFixedToolAnnotations(IList<McpClientTool> tools, params string[] excludedToolNames)
    {
        var expectedToolNames = KnownMcpTools.All
            .Except(excludedToolNames)
            .OrderBy(static name => name, StringComparer.Ordinal);
        Assert.Equal(
            expectedToolNames,
            tools.Select(static tool => tool.Name).OrderBy(static name => name, StringComparer.Ordinal));

        foreach (var tool in tools)
        {
            AssertToolAnnotations(
                tool,
                readOnly: tool.Name is not (KnownMcpTools.ExecuteResourceCommand or KnownMcpTools.SelectAppHost),
                destructive: tool.Name == KnownMcpTools.ExecuteResourceCommand);
        }
    }

    private static void AssertToolAnnotations(McpClientTool tool, bool readOnly, bool destructive)
    {
        var annotations = Assert.IsType<ToolAnnotations>(tool.ProtocolTool.Annotations);
        Assert.Equal(readOnly, annotations.ReadOnlyHint);
        Assert.Equal(destructive, annotations.DestructiveHint);
    }

    private static string GetResultText(CallToolResult result)
    {
        if (result.Content?.FirstOrDefault() is TextContentBlock textContent)
        {
            return textContent.Text;
        }

        return string.Empty;
    }

    private static JsonDocument GetMarkedJson(CallToolResult result, string marker)
    {
        var text = GetResultText(result);
        var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Result should contain the '{marker}' marker.");
        return JsonDocument.Parse(text[(markerIndex + marker.Length)..].Trim());
    }

    public enum ResourceToolContractMutation
    {
        Description,
        InputSchema,
        OutputSchema,
        AnnotationTitle,
        DestructiveHint,
        IdempotentHint,
        OpenWorldHint,
        ReadOnlyHint
    }
}

internal sealed class McpTestContext(
    McpClient client,
    CancellationTokenSource cts,
    TemporaryWorkspace workspace,
    Task serverRunTask,
    TestMcpServerTransport testTransport,
    AgentMcpCommand command,
    ServiceProvider serviceProvider,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    public McpClient Client => client;
    public CancellationTokenSource Cts => cts;
    public TemporaryWorkspace Workspace => workspace;
    public TestAuxiliaryBackchannelMonitor? BackchannelMonitor { get; init; }
    public AgentMcpCommand Command => command;

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
