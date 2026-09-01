// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREMCP001

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREINTERACTION001 // InteractionInput is used to test dashboard command arguments.

[Trait("Partition", "5")]
public class WithMcpServerTests
{
    [Fact]
    public void WithMcpServer_ThrowsArgumentNullException_WhenBuilderIsNull()
    {
        IResourceBuilder<ContainerResource> builder = null!;
        Assert.Throws<ArgumentNullException>(() => builder.WithMcpServer());
    }

    [Fact]
    public async Task WithMcpServer_AddsMcpServerEndpointAnnotation()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithHttpEndpoint(name: "http")
            .WithMcpServer(endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());

        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());
        Assert.NotNull(mcpAnnotation.EndpointUrlResolver);

        var command = Assert.Single(resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == "app-mcp-call-tool-interactive");
        Assert.Equal("Invoke MCP", command.DisplayName);
        Assert.Equal("ChatSparkle", command.IconName);
        Assert.Equal(IconVariant.Regular, command.IconVariant);
        Assert.True(command.IsHighlighted);
        Assert.Equal(ResourceCommandVisibility.UI, command.Visibility);
        Assert.Empty(command.Arguments);

        var commandWithArguments = Assert.Single(resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == "app-mcp-call-tool");
        Assert.Equal("Invoke MCP", commandWithArguments.DisplayName);
        Assert.Equal("Invoke an MCP tool by name with JSON arguments.", commandWithArguments.DisplayDescription);
        Assert.Equal("ChatSparkle", commandWithArguments.IconName);
        Assert.Equal(IconVariant.Regular, commandWithArguments.IconVariant);
        Assert.False(commandWithArguments.IsHighlighted);
        Assert.Equal(ResourceCommandVisibility.Api, commandWithArguments.Visibility);

        var arguments = commandWithArguments.Arguments.ToArray();
        Assert.Collection(
            arguments,
            tool =>
            {
                Assert.Equal("tool", tool.Name);
                Assert.Equal("Tool", tool.Label);
                Assert.Equal("Name of the MCP tool to invoke.", tool.Description);
                Assert.Equal(InputType.Text, tool.InputType);
                Assert.True(tool.Required);
            },
            toolArguments =>
            {
                Assert.Equal("arguments", toolArguments.Name);
                Assert.Equal("Arguments JSON", toolArguments.Label);
                Assert.Equal("JSON object to pass as the MCP tool arguments.", toolArguments.Description);
                Assert.Equal(InputType.Text, toolArguments.InputType);
                Assert.False(toolArguments.Required);
                Assert.Equal("{}", toolArguments.Value);
            });
    }

    [Fact]
    public async Task WithMcpServer_DoesNotHighlightCommandWhenResourceAlreadyHasHighlightedCommand()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithHttpEndpoint(name: "http")
            .WithCommand(
                "existing",
                "Existing",
                _ => Task.FromResult(CommandResults.Success()),
                new CommandOptions { IsHighlighted = true })
            .WithMcpServer(endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());

        var command = Assert.Single(resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == "app-mcp-call-tool-interactive");
        Assert.False(command.IsHighlighted);
    }

    [Fact]
    public async Task WithMcpServer_DefaultsToHttpsEndpoint()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("https", e =>
            {
                e.UriScheme = "https";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8443);
            })
            .WithHttpEndpoint(name: "http")
            .WithMcpServer();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("https://localhost:8443/mcp", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithMcpServer_FallsBackToHttpEndpoint()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("http://localhost:8080/mcp", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithMcpServer_SelectsHttpEndpointBySchemeWhenNameDiffers()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithHttpEndpoint(name: "api", targetPort: 8080)
            .WithEndpoint("api", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080))
            .WithMcpServer();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.Equal("http://localhost:8080/mcp", resolvedUri?.ToString());
    }

    [Fact]
    public async Task WithMcpServer_ImplicitEndpointSelectionSkipsExcludedEndpoints()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithHttpsEndpoint(name: "management")
            .WithEndpoint("management", e =>
            {
                e.ExcludeReferenceEndpoint = true;
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8443);
            })
            .WithHttpEndpoint(name: "api")
            .WithEndpoint("api", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080))
            .WithMcpServer();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.Equal("http://localhost:8080/mcp", resolvedUri?.ToString());
    }

    [Fact]
    public async Task WithMcpServer_ExplicitEndpointSelectionAllowsExcludedEndpoint()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithHttpsEndpoint(name: "management")
            .WithEndpoint("management", e =>
            {
                e.ExcludeReferenceEndpoint = true;
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8443);
            })
            .WithMcpServer(endpointName: "management");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.Equal("https://localhost:8443/mcp", resolvedUri?.ToString());
    }

    [Fact]
    public async Task WithMcpServer_ResolvesDefaultMcpPath()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer(endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("http://localhost:8080/mcp", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithMcpServer_ResolvesCustomPath()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer("/sse", endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("http://localhost:8080/sse", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithMcpServer_ResolvesNullPath()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddContainer("app", "image")
            .WithEndpoint("http", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer(path: null, endpointName: "http");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(appModel.Resources.OfType<ContainerResource>());
        var mcpAnnotation = Assert.Single(resource.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(resource, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        // Uri normalizes to include trailing slash for absolute URIs without path
        Assert.Equal("http://localhost:8080/", resolvedUri!.ToString());
    }

    [Fact]
    public void WithMcpServer_ReturnsBuilderForChaining()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var container = appBuilder.AddContainer("app", "image")
            .WithHttpEndpoint(name: "http");

        var result = container.WithMcpServer(endpointName: "http");

        Assert.Same(container, result);
    }

    [Fact]
    public async Task WithMcpServer_InvokeCommandUsesToolAndArguments()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var handler = new McpCommandHandler();
        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            container.Resource,
            "app-mcp-call-tool",
            CreateMcpArguments("get_weather", """{"location":"Seattle","units":"celsius"}""")).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal("MCP tool response received.", result.Message);
        Assert.NotNull(handler.ToolCallRequest);
        Assert.Equal("get_weather", handler.ToolCallRequest?["params"]?["name"]?.GetValue<string>());
        Assert.Equal("Seattle", handler.ToolCallRequest?["params"]?["arguments"]?["location"]?.GetValue<string>());
        Assert.Equal("celsius", handler.ToolCallRequest?["params"]?["arguments"]?["units"]?.GetValue<string>());
        Assert.Equal("session-1", handler.ToolCallSessionId);
        Assert.All(handler.ProtocolVersions, version => Assert.Equal("2025-06-18", version));
    }

    [Fact]
    public async Task WithMcpServer_InvokeCommandFindsToolOnLaterPage()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var handler = new McpCommandHandler(paginateTools: true);
        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            container.Resource,
            "app-mcp-call-tool",
            CreateMcpArguments("get_weather", """{"location":"Seattle"}""")).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal([null, "page-2"], handler.ToolListCursors);
    }

    [Theory]
    [InlineData(McpToolCallResponse.JsonRpcError, "MCP tool call returned a JSON-RPC error.")]
    [InlineData(McpToolCallResponse.ToolError, "MCP tool reported an error.")]
    public async Task WithMcpServer_InvokeCommandReportsProtocolErrors(McpToolCallResponse toolCallResponse, string expectedMessage)
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var handler = new McpCommandHandler(toolCallResponse: toolCallResponse);
        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            container.Resource,
            "app-mcp-call-tool",
            CreateMcpArguments("get_weather", """{"location":"Seattle"}""")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal(expectedMessage, result.Message);
    }

    [Fact]
    public async Task WithMcpServer_InvokeCommandSkipsSseNotificationsBeforeResponse()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var handler = new McpCommandHandler(useSseResponses: true);
        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            container.Resource,
            "app-mcp-call-tool",
            CreateMcpArguments("get_weather", """{"location":"Seattle"}""")).DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(handler.ToolCallRequest);
    }

    [Fact]
    public async Task WithMcpServer_InteractiveCommandOmitsOptionalParametersWithoutDefaults()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var interactionService = new TestInteractionService();
        appBuilder.Services.AddSingleton<IInteractionService>(interactionService);
        var handler = new McpCommandHandler(includeOptionalParameters: true);
        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var commandTask = app.ResourceCommands.ExecuteCommandAsync(container.Resource, "app-mcp-call-tool-interactive");

        var toolInteraction = await interactionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        var toolInput = Assert.Single(toolInteraction.Inputs);
        toolInput.Value = "get_weather";
        toolInteraction.CompletionTcs.SetResult(InteractionResult.Ok(toolInput));

        var argumentsInteraction = await interactionService.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Collection(
            argumentsInteraction.Inputs,
            optionalBoolean =>
            {
                Assert.Equal("include_details", optionalBoolean.Name);
                Assert.Null(optionalBoolean.Value);
            },
            optionalArray =>
            {
                Assert.Equal("tags", optionalArray.Name);
                Assert.Null(optionalArray.Value);
            },
            optionalObject =>
            {
                Assert.Equal("metadata", optionalObject.Name);
                Assert.Null(optionalObject.Value);
            },
            defaultedBoolean =>
            {
                Assert.Equal("use_cache", defaultedBoolean.Name);
                Assert.Equal("true", defaultedBoolean.Value);
            });
        argumentsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(argumentsInteraction.Inputs));

        var result = await commandTask.DefaultTimeout();

        Assert.True(result.Success);
        var arguments = Assert.IsType<JsonObject>(handler.ToolCallRequest?["params"]?["arguments"]);
        Assert.Equal(["use_cache"], arguments.Select(argument => argument.Key));
        Assert.True(arguments["use_cache"]?.GetValue<bool>() is true);
    }

    [Fact]
    public async Task WithMcpServer_InvokeInteractiveCommandWithoutArgumentsPromptsForTool()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => new McpCommandHandler());

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(container.Resource, "app-mcp-call-tool-interactive").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("MCP tool argument is required.", result.Message);
    }

    [Fact]
    public async Task WithMcpServer_InvokeCommandRequiresToolArgument()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => new McpCommandHandler());

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(container.Resource, "app-mcp-call-tool").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Command argument validation failed.", result.Message);
    }

    [Fact]
    public async Task WithMcpServer_InvokeCommandRejectsUnknownTool()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var handler = new McpCommandHandler();
        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            container.Resource,
            "app-mcp-call-tool",
            CreateMcpArguments("unknown_tool", "{}")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("MCP server did not return a tool named 'unknown_tool'. Available tools: get_weather.", result.Message);
        Assert.Null(handler.ToolCallRequest);
    }

    [Fact]
    public async Task WithMcpServer_InvokeCommandRejectsInvalidArgumentsJson()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var handler = new McpCommandHandler();
        appBuilder.Services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var container = AddMcpContainer(appBuilder);

        using var app = appBuilder.Build();
        await app.StartAsync().DefaultTimeout();
        await MoveResourceToRunningStateAsync(app, container.Resource).DefaultTimeout();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            container.Resource,
            "app-mcp-call-tool",
            CreateMcpArguments("get_weather", "not-json")).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("MCP tool arguments must be a valid JSON object.", result.Message);
        Assert.Null(handler.ToolCallRequest);
    }

    private static IResourceBuilder<ContainerResource> AddMcpContainer(IDistributedApplicationBuilder appBuilder)
    {
        return appBuilder.AddContainer("app", "image")
            .WithHttpEndpoint(name: "http", targetPort: 8080)
            .WithEndpoint("http", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 8080);
            })
            .WithMcpServer(endpointName: "http");
    }

    private static InteractionInputCollection CreateMcpArguments(string tool, string arguments)
    {
        return new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "tool",
                InputType = InputType.Text,
                Value = tool
            },
            new InteractionInput
            {
                Name = "arguments",
                InputType = InputType.Text,
                Value = arguments
            }
        ]);
    }

    private static async Task MoveResourceToRunningStateAsync(DistributedApplication app, IResource resource)
    {
        await app.ResourceNotifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        await app.ResourceNotifications.WaitForResourceAsync(
            resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Running &&
                 e.Snapshot.Commands.FirstOrDefault(c => c.Name == "app-mcp-call-tool-interactive")?.State == ResourceCommandState.Enabled).DefaultTimeout();
    }

    public enum McpToolCallResponse
    {
        Success,
        JsonRpcError,
        ToolError
    }

    private sealed class McpCommandHandler(
        bool paginateTools = false,
        McpToolCallResponse toolCallResponse = McpToolCallResponse.Success,
        bool useSseResponses = false,
        bool includeOptionalParameters = false) : HttpMessageHandler
    {
        public JsonObject? ToolCallRequest { get; private set; }

        public string? ToolCallSessionId { get; private set; }

        public List<string?> ProtocolVersions { get; } = [];

        public List<string?> ToolListCursors { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            var payload = string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body) as JsonObject;
            var method = payload?["method"]?.GetValue<string>();
            if (method is not "initialize")
            {
                ProtocolVersions.Add(request.Headers.TryGetValues("MCP-Protocol-Version", out var versions) ? versions.Single() : null);
            }

            return method switch
            {
                "initialize" => CreateJsonRpcResponse(
                    payload?["id"],
                    new JsonObject
                    {
                        ["protocolVersion"] = "2025-06-18",
                        ["capabilities"] = new JsonObject()
                    },
                    sessionId: "session-1"),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => HandleToolsList(payload!),
                "tools/call" => HandleToolCall(payload!, request),
                _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent($$"""{"error":"Unexpected method '{{method}}'."}""", Encoding.UTF8, "application/json")
                }
            };
        }

        private HttpResponseMessage HandleToolsList(JsonObject payload)
        {
            var cursor = payload["params"]?["cursor"]?.GetValue<string>();
            ToolListCursors.Add(cursor);

            if (paginateTools && cursor is null)
            {
                return CreateJsonRpcResponse(
                    payload["id"],
                    new JsonObject
                    {
                        ["tools"] = new JsonArray(CreateTool("first_tool")),
                        ["nextCursor"] = "page-2"
                    });
            }

            return CreateJsonRpcResponse(
                payload["id"],
                new JsonObject
                {
                    ["tools"] = new JsonArray(CreateTool("get_weather"))
                });
        }

        private JsonObject CreateTool(string name)
        {
            var properties = includeOptionalParameters
                ? new JsonObject
                {
                    ["include_details"] = new JsonObject { ["type"] = "boolean" },
                    ["tags"] = new JsonObject { ["type"] = "array" },
                    ["metadata"] = new JsonObject { ["type"] = "object" },
                    ["use_cache"] = new JsonObject { ["type"] = "boolean", ["default"] = true }
                }
                : new JsonObject
                {
                    ["location"] = new JsonObject { ["type"] = "string" },
                    ["units"] = new JsonObject { ["type"] = "string" }
                };

            return new JsonObject
            {
                ["name"] = name,
                ["description"] = $"Gets data from {name}.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties
                }
            };
        }

        private HttpResponseMessage HandleToolCall(JsonObject payload, HttpRequestMessage request)
        {
            ToolCallRequest = payload;
            ToolCallSessionId = request.Headers.TryGetValues("Mcp-Session-Id", out var values)
                ? values.Single()
                : null;

            return toolCallResponse switch
            {
                McpToolCallResponse.JsonRpcError => CreateJsonRpcErrorResponse(payload["id"]),
                McpToolCallResponse.ToolError => CreateJsonRpcResponse(
                    payload["id"],
                    new JsonObject
                    {
                        ["isError"] = true,
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Tool failed." })
                    }),
                _ => CreateJsonRpcResponse(
                    payload["id"],
                    new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Sunny in Seattle." })
                    })
            };
        }

        private HttpResponseMessage CreateJsonRpcErrorResponse(JsonNode? id)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32000,
                    ["message"] = "Tool failed."
                }
            };
            return CreateResponse(response);
        }

        private HttpResponseMessage CreateJsonRpcResponse(JsonNode? id, JsonObject result, string? sessionId = null)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = result
            };
            return CreateResponse(response, sessionId);
        }

        private HttpResponseMessage CreateResponse(JsonObject payload, string? sessionId = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = useSseResponses
                    ? new StringContent(
                        $$$"""
                         event: message
                         data: {"jsonrpc":"2.0","method":"notifications/progress","params":{}}

                         event: message
                         data: {{{payload.ToJsonString()}}}

                         """,
                        Encoding.UTF8,
                        "text/event-stream")
                    : new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            if (sessionId is not null)
            {
                response.Headers.Add("Mcp-Session-Id", sessionId);
            }

            return response;
        }
    }

#pragma warning restore ASPIREINTERACTION001
}
