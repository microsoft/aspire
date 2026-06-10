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

    private sealed class McpCommandHandler : HttpMessageHandler
    {
        public JsonObject? ToolCallRequest { get; private set; }

        public string? ToolCallSessionId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            var payload = string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body) as JsonObject;
            var method = payload?["method"]?.GetValue<string>();

            return method switch
            {
                "initialize" => CreateJsonResponse("""{"jsonrpc":"2.0","id":"1","result":{"protocolVersion":"2025-06-18","capabilities":{}}}""", sessionId: "session-1"),
                "notifications/initialized" => CreateJsonResponse("""{"jsonrpc":"2.0","result":{}}"""),
                "tools/list" => CreateJsonResponse(
                    """
                    {
                      "jsonrpc": "2.0",
                      "id": "2",
                      "result": {
                        "tools": [
                          {
                            "name": "get_weather",
                            "description": "Gets the weather.",
                            "inputSchema": {
                              "type": "object",
                              "properties": {
                                "location": { "type": "string" },
                                "units": { "type": "string" }
                              }
                            }
                          }
                        ]
                      }
                    }
                    """),
                "tools/call" => HandleToolCall(payload!, request),
                _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent($$"""{"error":"Unexpected method '{{method}}'."}""", Encoding.UTF8, "application/json")
                }
            };
        }

        private HttpResponseMessage HandleToolCall(JsonObject payload, HttpRequestMessage request)
        {
            ToolCallRequest = payload;
            ToolCallSessionId = request.Headers.TryGetValues("Mcp-Session-Id", out var values)
                ? values.Single()
                : null;

            return CreateJsonResponse(
                """
                {
                  "jsonrpc": "2.0",
                  "id": "3",
                  "result": {
                    "content": [
                      { "type": "text", "text": "Sunny in Seattle." }
                    ]
                  }
                }
                """);
        }

        private static HttpResponseMessage CreateJsonResponse(string json, string? sessionId = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
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
