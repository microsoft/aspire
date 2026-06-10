// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is used to prompt for dashboard command input.

/// <summary>
/// Provides extension methods for configuring MCP (Model Context Protocol) server endpoints on resources.
/// </summary>
public static class McpServerResourceBuilderExtensions
{
    private static readonly string[] s_httpSchemes = ["https", "http"];
    private const string McpToolArgumentName = "tool";
    private const string McpToolArgumentsArgumentName = "arguments";

    /// <summary>
    /// Marks the resource as hosting a Model Context Protocol (MCP) server on the specified endpoint.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="path">An optional path to append to the endpoint URL when forming the MCP server address. Defaults to <c>"/mcp"</c>.</param>
    /// <param name="endpointName">An optional name of the endpoint that hosts the MCP server. If not specified, defaults to the first HTTPS or HTTP endpoint.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// This method adds an <see cref="McpServerEndpointAnnotation"/> to the resource, enabling the Aspire tooling
    /// to discover, proxy, and invoke the MCP server exposed by the resource.
    /// </remarks>
    /// <example>
    /// Mark a resource as hosting an MCP server using the default endpoint:
    /// <code>
    /// var api = builder.AddProject&lt;Projects.MyApi&gt;("api")
    ///     .WithMcpServer();
    /// </code>
    /// Mark a resource as hosting an MCP server with a custom path and endpoint:
    /// <code>
    /// var api = builder.AddProject&lt;Projects.MyApi&gt;("api")
    ///     .WithMcpServer("/sse", endpointName: "https");
    /// </code>
    /// </example>
    [Experimental("ASPIREMCP001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IResourceBuilder<T> WithMcpServer<T>(
        this IResourceBuilder<T> builder,
        string? path = "/mcp",
        [EndpointName] string? endpointName = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        var isHighlighted = !builder.Resource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .Any(command => command.IsHighlighted);

        return WithMcpServer(builder, path, endpointName, isHighlighted);
    }

    internal static IResourceBuilder<T> WithMcpServer<T>(
        this IResourceBuilder<T> builder,
        string? path,
        string? endpointName,
        bool isHighlighted)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new McpServerEndpointAnnotation(async (resource, cancellationToken) =>
        {
            var endpoint = GetMcpEndpoint(resource, endpointName);

            if (!endpoint.Exists)
            {
                return null;
            }

            var baseUrl = await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(baseUrl))
            {
                return null;
            }

            if (string.IsNullOrEmpty(path))
            {
                return new Uri(baseUrl, UriKind.Absolute);
            }

            var normalizedPath = path;
            if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
            {
                normalizedPath = "/" + normalizedPath;
            }

            var combined = baseUrl.TrimEnd('/') + normalizedPath;
            return new Uri(combined, UriKind.Absolute);
        }));

        AddMcpEndpointUrl(builder, path, endpointName);
        AddMcpInvokeCommandIfMissing(builder, path, endpointName, isHighlighted);

        return builder;
    }

    private static EndpointReference GetMcpEndpoint(IResourceWithEndpoints resource, string? endpointName)
    {
        var endpoints = resource.GetEndpoints();

        if (endpointName is not null)
        {
            return endpoints.FirstOrDefault(e => string.Equals(e.EndpointName, endpointName, StringComparisons.EndpointAnnotationName))
                ?? throw new DistributedApplicationException(
                    $"Could not create MCP server for resource '{resource.Name}' as no endpoint was found with name '{endpointName}'.");
        }

        foreach (var scheme in s_httpSchemes)
        {
            var endpoint = endpoints.FirstOrDefault(e => string.Equals(e.EndpointName, scheme, StringComparisons.EndpointAnnotationName));
            if (endpoint is not null)
            {
                return endpoint;
            }
        }

        throw new DistributedApplicationException(
            $"Could not create MCP server for resource '{resource.Name}' as no endpoint was found matching one of the specified names: {string.Join(", ", s_httpSchemes)}");
    }

    private static void AddMcpEndpointUrl<T>(IResourceBuilder<T> builder, string? path, string? endpointName)
        where T : IResourceWithEndpoints
    {
        builder.WithUrls(context =>
        {
            EndpointReference endpoint;
            try
            {
                endpoint = GetMcpEndpoint(builder.Resource, endpointName);
            }
            catch (DistributedApplicationException ex)
            {
                context.Logger.LogWarning(ex, "Could not add MCP endpoint URL for resource '{ResourceName}'.", builder.Resource.Name);
                return;
            }

            if (!endpoint.Exists)
            {
                context.Logger.LogWarning("Could not add MCP endpoint URL as endpoint '{EndpointName}' was not found on resource '{ResourceName}'.", endpoint.EndpointName, builder.Resource.Name);
                return;
            }

            context.Urls.Add(new ResourceUrlAnnotation
            {
                Url = path ?? string.Empty,
                DisplayText = "MCP Endpoint"
            }.WithEndpoint(endpoint));
        });
    }

    private static void AddMcpInvokeCommandIfMissing<T>(IResourceBuilder<T> builder, string? path, string? endpointName, bool isHighlighted)
        where T : IResourceWithEndpoints
    {
        var interactiveCommandName = $"{builder.Resource.Name}-mcp-call-tool-interactive";
        if (builder.Resource.Annotations.OfType<ResourceCommandAnnotation>().Any(c => string.Equals(c.Name, interactiveCommandName, StringComparison.Ordinal)))
        {
            return;
        }

        builder.WithHttpCommand(
            path ?? string.Empty,
            "Invoke MCP",
            endpointSelector: () => GetMcpEndpoint(builder.Resource, endpointName),
            commandName: interactiveCommandName,
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Post,
                IconName = "ChatSparkle",
                IconVariant = IconVariant.Regular,
                IsHighlighted = isHighlighted,
                PrepareRequest = PrepareMcpToolCallRequestAsync,
                GetCommandResult = GetMcpCommandResultAsync,
                Visibility = ResourceCommandVisibility.UI
            });

        var commandWithArgumentsName = $"{builder.Resource.Name}-mcp-call-tool";
        if (builder.Resource.Annotations.OfType<ResourceCommandAnnotation>().Any(c => string.Equals(c.Name, commandWithArgumentsName, StringComparison.Ordinal)))
        {
            return;
        }

        builder.WithHttpCommand(
            path ?? string.Empty,
            "Invoke MCP",
            endpointSelector: () => GetMcpEndpoint(builder.Resource, endpointName),
            commandName: commandWithArgumentsName,
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Post,
                Description = "Invoke an MCP tool by name with JSON arguments.",
                IconName = "ChatSparkle",
                IconVariant = IconVariant.Regular,
                Arguments = [CreateMcpToolArgument(), CreateMcpArgumentsArgument()],
                PrepareRequest = PrepareMcpToolCallRequestAsync,
                GetCommandResult = GetMcpCommandResultAsync,
                Visibility = ResourceCommandVisibility.Api
            });
    }

    private static async Task PrepareMcpToolCallRequestAsync(HttpCommandRequestContext ctx)
    {
        var initializeResponse = await SendMcpJsonRpcRequestAsync(ctx, CreateMcpInitializeRequest(), sessionId: null).ConfigureAwait(true);
        using var initializeHttpResponse = initializeResponse.Response;
        var sessionId = initializeResponse.Response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds) ? sessionIds.FirstOrDefault() : null;

        await SendMcpJsonRpcNotificationAsync(ctx, "notifications/initialized", sessionId).ConfigureAwait(true);

        var toolsListResponse = await SendMcpJsonRpcRequestAsync(
            ctx,
            CreateMcpJsonRpcRequest("tools/list", new JsonObject()),
            sessionId).ConfigureAwait(true);
        using var toolsListHttpResponse = toolsListResponse.Response;

        var tools = ReadMcpTools(toolsListResponse.Payload);
        if (tools.Count == 0)
        {
            throw new InvalidOperationException("MCP server did not return any tools.");
        }

        var (selectedTool, arguments) = await GetMcpToolCallAsync(ctx, tools).ConfigureAwait(true);

        ConfigureMcpRequest(
            ctx.Request,
            CreateMcpJsonRpcRequest(
                "tools/call",
                new JsonObject
                {
                    ["name"] = selectedTool.Name,
                    ["arguments"] = arguments
                }),
            sessionId);
    }

    private static InteractionInput CreateMcpToolArgument()
    {
        return new InteractionInput
        {
            Name = McpToolArgumentName,
            Label = "Tool",
            Description = "Name of the MCP tool to invoke.",
            InputType = InputType.Text,
            Required = true,
            Placeholder = "get_weather"
        };
    }

    private static InteractionInput CreateMcpArgumentsArgument()
    {
        return new InteractionInput
        {
            Name = McpToolArgumentsArgumentName,
            Label = "Arguments JSON",
            Description = "JSON object to pass as the MCP tool arguments.",
            InputType = InputType.Text,
            Required = false,
            Value = "{}",
            Placeholder = "{ \"location\": \"Seattle\" }"
        };
    }

    private static async Task<ExecuteCommandResult> GetMcpCommandResultAsync(HttpCommandResultContext ctx)
    {
        ctx.CancellationToken.ThrowIfCancellationRequested();

        var responseBody = await ctx.Response.Content.ReadAsStringAsync(ctx.CancellationToken).ConfigureAwait(true);
        if (!ctx.Response.IsSuccessStatusCode)
        {
            return CommandResults.Failure(
                $"MCP tool call failed with status code {(int)ctx.Response.StatusCode} ({ctx.Response.StatusCode}).",
                responseBody,
                CommandResultFormat.Text);
        }

        var result = TryExtractServerSentEventData(responseBody, out var sseData) ? sseData : responseBody;
        try
        {
            var responseJson = JsonNode.Parse(result);
            if (responseJson is not null)
            {
                return CommandResults.Success(
                    message: "MCP tool response received.",
                    result: JsonSerializer.Serialize(responseJson, s_indentedJsonOptions),
                    resultFormat: CommandResultFormat.Json,
                    displayImmediately: true);
            }
        }
        catch (JsonException)
        {
            // Some MCP servers can return plain text transport errors. Surface the response as-is
            // instead of failing result processing after the HTTP request succeeded.
        }

        return CommandResults.Success(
            message: "MCP tool response received.",
            result: result,
            resultFormat: CommandResultFormat.Text,
            displayImmediately: true);
    }

    private static JsonObject CreateMcpInitializeRequest()
    {
        return CreateMcpJsonRpcRequest(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "Aspire Dashboard",
                    ["version"] = "1.0"
                }
            });
    }

    private static JsonObject CreateMcpJsonRpcRequest(string method, JsonObject parameters)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = method,
            ["params"] = parameters
        };
    }

    private static JsonObject CreateMcpJsonRpcNotification(string method)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };
    }

    private static async Task<(HttpResponseMessage Response, JsonObject Payload)> SendMcpJsonRpcRequestAsync(HttpCommandRequestContext ctx, JsonObject request, string? sessionId)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, ctx.Request.RequestUri);
        ConfigureMcpRequest(requestMessage, request, sessionId);

        var response = await ctx.HttpClient.SendAsync(requestMessage, ctx.CancellationToken).ConfigureAwait(true);
        var payload = await ReadMcpJsonRpcPayloadAsync(response, ctx.CancellationToken).ConfigureAwait(true);
        return (response, payload);
    }

    private static async Task SendMcpJsonRpcNotificationAsync(HttpCommandRequestContext ctx, string method, string? sessionId)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, ctx.Request.RequestUri);
        ConfigureMcpRequest(requestMessage, CreateMcpJsonRpcNotification(method), sessionId);

        using var response = await ctx.HttpClient.SendAsync(requestMessage, ctx.CancellationToken).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            var errorPayload = await response.Content.ReadAsStringAsync(ctx.CancellationToken).ConfigureAwait(true);
            throw new InvalidOperationException($"MCP notification '{method}' failed with status code {(int)response.StatusCode} ({response.StatusCode}): {errorPayload}");
        }
    }

    private static void ConfigureMcpRequest(HttpRequestMessage request, JsonObject payload, string? sessionId)
    {
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonObject> ReadMcpJsonRpcPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MCP request failed with status code {(int)response.StatusCode} ({response.StatusCode}): {responseBody}");
        }

        var payload = TryExtractServerSentEventData(responseBody, out var sseData) ? sseData : responseBody;
        return JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidOperationException("MCP server returned an empty or invalid JSON-RPC response.");
    }

    private static IReadOnlyList<McpTool> ReadMcpTools(JsonObject payload)
    {
        var tools = payload["result"]?["tools"] as JsonArray;
        if (tools is null)
        {
            return [];
        }

        var result = new List<McpTool>();
        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool || tool["name"]?.GetValue<string>() is not { Length: > 0 } name)
            {
                continue;
            }

            result.Add(new McpTool(
                name,
                tool["description"]?.GetValue<string>(),
                tool["inputSchema"] as JsonObject));
        }

        return result;
    }

    private static McpTool GetSelectedMcpTool(InteractionInputCollection arguments, IReadOnlyList<McpTool> tools)
    {
        var toolName = GetMcpArgumentValue(arguments, McpToolArgumentName)
            ?? throw new InvalidOperationException("MCP tool argument is required.");
        var selectedTool = tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        if (selectedTool is not null)
        {
            return selectedTool;
        }

        var availableTools = string.Join(", ", tools.Select(tool => tool.Name));
        throw new InvalidOperationException($"MCP server did not return a tool named '{toolName}'. Available tools: {availableTools}.");
    }

    private static JsonObject GetMcpToolArguments(InteractionInputCollection arguments)
    {
        var value = GetMcpArgumentValue(arguments, McpToolArgumentsArgumentName);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : TryParseJsonObject(value, out var result)
                ? result
                : throw new InvalidOperationException("MCP tool arguments must be a valid JSON object.");
    }

    private static async Task<(McpTool Tool, JsonObject Arguments)> GetMcpToolCallAsync(HttpCommandRequestContext ctx, IReadOnlyList<McpTool> tools)
    {
        var interactionService = ctx.Services.GetRequiredService<IInteractionService>();
        if (!string.IsNullOrWhiteSpace(GetMcpArgumentValue(ctx.Arguments, McpToolArgumentName)) || !interactionService.IsAvailable)
        {
            var selectedTool = GetSelectedMcpTool(ctx.Arguments, tools);
            return (selectedTool, GetMcpToolArguments(ctx.Arguments));
        }

        var promptedTool = await PromptForMcpToolAsync(ctx, tools).ConfigureAwait(true);
        return (promptedTool, await PromptForMcpToolArgumentsAsync(ctx, promptedTool).ConfigureAwait(true));
    }

    private static string? GetMcpArgumentValue(InteractionInputCollection arguments, string name)
    {
        return arguments.TryGetByName(name, out var input) ? input.Value : null;
    }

    private static async Task<McpTool> PromptForMcpToolAsync(HttpCommandRequestContext ctx, IReadOnlyList<McpTool> tools)
    {
        var interactionService = ctx.Services.GetRequiredService<IInteractionService>();
        var toolInput = new InteractionInput
        {
            Name = McpToolArgumentName,
            Label = "Tool",
            InputType = InputType.Choice,
            Required = true,
            Options = tools.Select(tool => new KeyValuePair<string, string>(tool.Name, string.IsNullOrWhiteSpace(tool.Description) ? tool.Name : $"{tool.Name} - {tool.Description}")).ToArray()
        };

        var result = await interactionService.PromptInputAsync(
            title: "MCP Tool",
            message: "Choose the MCP tool to invoke.",
            input: toolInput,
            cancellationToken: ctx.CancellationToken).ConfigureAwait(true);

        if (result.Canceled || string.IsNullOrWhiteSpace(result.Data.Value))
        {
            ctx.HttpClient.CancelPendingRequests();
            throw new OperationCanceledException("User canceled the MCP tool prompt.");
        }

        return tools.First(tool => string.Equals(tool.Name, result.Data.Value, StringComparison.Ordinal));
    }

    private static async Task<JsonObject> PromptForMcpToolArgumentsAsync(HttpCommandRequestContext ctx, McpTool tool)
    {
        var parameterInputs = CreateMcpToolParameterInputs(tool);
        if (parameterInputs.Count > 0)
        {
            return await PromptForMcpToolParameterInputsAsync(ctx, tool, parameterInputs).ConfigureAwait(true);
        }

        if (tool.InputSchema?["properties"] is JsonObject { Count: 0 })
        {
            return [];
        }

        return await PromptForMcpToolRawArgumentsAsync(ctx, tool).ConfigureAwait(true);
    }

    private static async Task<JsonObject> PromptForMcpToolParameterInputsAsync(HttpCommandRequestContext ctx, McpTool tool, IReadOnlyList<McpToolParameterInput> parameterInputs)
    {
        var interactionService = ctx.Services.GetRequiredService<IInteractionService>();
        var result = await interactionService.PromptInputsAsync(
            title: $"Invoke {tool.Name}",
            message: "Enter the tool arguments.",
            inputs: parameterInputs.Select(p => p.Input).ToArray(),
            options: new InputsDialogInteractionOptions
            {
                ValidationCallback = context =>
                {
                    foreach (var parameterInput in parameterInputs)
                    {
                        var input = context.Inputs[parameterInput.Input.Name];
                        if (string.IsNullOrWhiteSpace(input.Value) && !parameterInput.Input.Required)
                        {
                            continue;
                        }

                        if (!TryCreateMcpToolArgumentValue(parameterInput.Parameter, input.Value, out _, out var errorMessage))
                        {
                            context.AddValidationError(input, errorMessage);
                        }
                    }

                    return Task.CompletedTask;
                }
            },
            cancellationToken: ctx.CancellationToken).ConfigureAwait(true);

        if (result.Canceled)
        {
            ctx.HttpClient.CancelPendingRequests();
            throw new OperationCanceledException("User canceled the MCP arguments prompt.");
        }

        var arguments = new JsonObject();
        foreach (var parameterInput in parameterInputs)
        {
            var input = result.Data[parameterInput.Input.Name];
            if (string.IsNullOrWhiteSpace(input.Value) && !parameterInput.Input.Required)
            {
                continue;
            }

            if (!TryCreateMcpToolArgumentValue(parameterInput.Parameter, input.Value, out var value, out var errorMessage))
            {
                throw new InvalidOperationException(errorMessage);
            }

            arguments[parameterInput.Parameter.Name] = value;
        }

        return arguments;
    }

    private static async Task<JsonObject> PromptForMcpToolRawArgumentsAsync(HttpCommandRequestContext ctx, McpTool tool)
    {
        var interactionService = ctx.Services.GetRequiredService<IInteractionService>();
        var argumentsInput = new InteractionInput
        {
            Name = McpToolArgumentsArgumentName,
            Label = "Arguments JSON",
            InputType = InputType.Text,
            Required = true,
            Value = CreateMcpToolArgumentsTemplate(tool).ToString(),
            Placeholder = "{ \"question\": \"What can you help with?\" }",
            Description = CreateMcpToolArgumentsDescription(tool),
            EnableDescriptionMarkdown = true
        };

        var result = await interactionService.PromptInputAsync(
            title: $"Invoke {tool.Name}",
            message: "Enter the JSON object to pass as the tool arguments.",
            input: argumentsInput,
            options: new InputsDialogInteractionOptions
            {
                ValidationCallback = context =>
                {
                    var arguments = context.Inputs[McpToolArgumentsArgumentName];
                    if (!TryParseJsonObject(arguments.Value, out _))
                    {
                        context.AddValidationError(arguments, "Arguments must be a valid JSON object.");
                    }

                    return Task.CompletedTask;
                }
            },
            cancellationToken: ctx.CancellationToken).ConfigureAwait(true);

        if (result.Canceled || string.IsNullOrWhiteSpace(result.Data.Value))
        {
            ctx.HttpClient.CancelPendingRequests();
            throw new OperationCanceledException("User canceled the MCP arguments prompt.");
        }

        return TryParseJsonObject(result.Data.Value, out var arguments)
            ? arguments
            : throw new InvalidOperationException("Arguments must be a valid JSON object.");
    }

    private static IReadOnlyList<McpToolParameterInput> CreateMcpToolParameterInputs(McpTool tool)
    {
        if (tool.InputSchema?["properties"] is not JsonObject properties || properties.Count == 0)
        {
            return [];
        }

        var requiredParameters = ReadMcpRequiredParameters(tool.InputSchema);
        var inputs = new List<McpToolParameterInput>();
        foreach (var property in properties)
        {
            if (property.Value is not JsonObject propertySchema)
            {
                return [];
            }

            var parameter = new McpToolParameter(
                property.Key,
                GetMcpJsonSchemaType(propertySchema),
                propertySchema,
                requiredParameters.Contains(property.Key));

            inputs.Add(new McpToolParameterInput(parameter, CreateMcpToolParameterInput(parameter)));
        }

        return inputs;
    }

    private static InteractionInput CreateMcpToolParameterInput(McpToolParameter parameter)
    {
        var schema = parameter.Schema;
        var inputType = parameter.Type switch
        {
            "boolean" => InputType.Boolean,
            "integer" or "number" => InputType.Number,
            _ when schema["enum"] is JsonArray enumValues && enumValues.All(value => value is not null) => InputType.Choice,
            _ => InputType.Text
        };

        var input = new InteractionInput
        {
            Name = parameter.Name,
            Label = parameter.Name,
            InputType = inputType,
            Required = parameter.Required,
            Value = GetMcpToolParameterDefaultValue(parameter),
            Placeholder = GetMcpToolParameterPlaceholder(parameter),
            Description = schema["description"]?.GetValue<string>(),
        };

        if (inputType == InputType.Choice && schema["enum"] is JsonArray choices)
        {
            input.Options = choices
                .Where(value => value is not null)
                .Select(value =>
                {
                    var option = GetMcpJsonValueAsString(value!);
                    return new KeyValuePair<string, string>(option, option);
                })
                .ToArray();
        }

        return input;
    }

    private static string? GetMcpToolParameterDefaultValue(McpToolParameter parameter)
    {
        if (parameter.Schema["default"] is { } defaultValue)
        {
            return defaultValue switch
            {
                JsonValue value when value.TryGetValue<string>(out var stringValue) => stringValue,
                _ => defaultValue.ToJsonString()
            };
        }

        return parameter.Type switch
        {
            "boolean" => "false",
            "integer" or "number" => null,
            "array" => "[]",
            "object" => "{}",
            _ => null
        };
    }

    private static string? GetMcpToolParameterPlaceholder(McpToolParameter parameter)
    {
        return parameter.Type switch
        {
            "boolean" => "true",
            "integer" => "42",
            "number" => "42.0",
            "array" => "[\"value\"]",
            "object" => "{ \"key\": \"value\" }",
            _ => "value"
        };
    }

    private static bool TryCreateMcpToolArgumentValue(McpToolParameter parameter, string? value, [NotNullWhen(true)] out JsonNode? result, [NotNullWhen(false)] out string? errorMessage)
    {
        result = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            if (parameter.Required)
            {
                errorMessage = $"{parameter.Name} is required.";
                return false;
            }

            result = JsonValue.Create(string.Empty);
            return true;
        }

        switch (parameter.Type)
        {
            case "boolean":
                if (bool.TryParse(value, out var boolValue))
                {
                    result = JsonValue.Create(boolValue);
                    return true;
                }

                errorMessage = $"{parameter.Name} must be true or false.";
                return false;

            case "integer":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    result = JsonValue.Create(longValue);
                    return true;
                }

                errorMessage = $"{parameter.Name} must be an integer.";
                return false;

            case "number":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    result = JsonValue.Create(doubleValue);
                    return true;
                }

                errorMessage = $"{parameter.Name} must be a number.";
                return false;

            case "array":
                return TryParseJsonNode(value, JsonValueKind.Array, parameter.Name, out result, out errorMessage);

            case "object":
                return TryParseJsonNode(value, JsonValueKind.Object, parameter.Name, out result, out errorMessage);

            default:
                result = JsonValue.Create(value);
                return true;
        }
    }

    private static bool TryParseJsonNode(string value, JsonValueKind expectedKind, string parameterName, [NotNullWhen(true)] out JsonNode? result, [NotNullWhen(false)] out string? errorMessage)
    {
        result = null;
        errorMessage = null;

        try
        {
            result = JsonNode.Parse(value);
            if (result is null || result.GetValueKind() != expectedKind)
            {
                errorMessage = $"{parameterName} must be a JSON {expectedKind.ToString().ToLowerInvariant()}.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            errorMessage = $"{parameterName} must be valid JSON.";
            return false;
        }
    }

    private static HashSet<string> ReadMcpRequiredParameters(JsonObject schema)
    {
        var requiredParameters = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is not JsonArray required)
        {
            return requiredParameters;
        }

        foreach (var requiredParameter in required)
        {
            if (requiredParameter?.GetValue<string>() is { } name)
            {
                requiredParameters.Add(name);
            }
        }

        return requiredParameters;
    }

    private static string? GetMcpJsonSchemaType(JsonObject schema)
    {
        if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var type))
        {
            return type;
        }

        return null;
    }

    private static string GetMcpJsonValueAsString(JsonNode value)
    {
        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
            _ => value.ToJsonString()
        };
    }

    private static JsonObject CreateMcpToolArgumentsTemplate(McpTool tool)
    {
        var result = new JsonObject();
        if (tool.InputSchema?["properties"] is not JsonObject properties)
        {
            return result;
        }

        foreach (var property in properties)
        {
            result[property.Key] = GetMcpDefaultArgumentValue(property.Value as JsonObject);
        }

        return result;
    }

    private static JsonNode? GetMcpDefaultArgumentValue(JsonObject? propertySchema)
    {
        var type = propertySchema?["type"]?.GetValue<string>();
        return type switch
        {
            "boolean" => false,
            "integer" or "number" => 0,
            "array" => new JsonArray(),
            "object" => new JsonObject(),
            _ => ""
        };
    }

    private static string CreateMcpToolArgumentsDescription(McpTool tool)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(tool.Description))
        {
            builder.AppendLine(tool.Description);
            builder.AppendLine();
        }

        if (tool.InputSchema is not null)
        {
            builder.AppendLine("Input schema:");
            builder.AppendLine("```json");
            builder.AppendLine(JsonSerializer.Serialize(tool.InputSchema, s_indentedJsonOptions));
            builder.AppendLine("```");
        }

        return builder.ToString();
    }

    private static bool TryParseJsonObject(string? value, [NotNullWhen(true)] out JsonObject? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            result = JsonNode.Parse(value) as JsonObject;
            return result is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractServerSentEventData(string responseBody, out string data)
    {
        // MCP streamable HTTP responses can arrive as a single SSE message:
        //   event: message
        //   data: {"jsonrpc":"2.0","id":"...","result":{...}}
        // Join consecutive data lines from the first event and leave JSON parsing to the caller.
        using var reader = new StringReader(responseBody);
        var builder = new StringBuilder();
        var sawData = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                if (sawData)
                {
                    break;
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["data:".Length..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (sawData)
            {
                builder.Append('\n');
            }

            builder.Append(value);
            sawData = true;
        }

        data = builder.ToString();
        return sawData;
    }

    private sealed record McpTool(string Name, string? Description, JsonObject? InputSchema);

    private sealed record McpToolParameter(string Name, string? Type, JsonObject Schema, bool Required);

    private sealed record McpToolParameterInput(McpToolParameter Parameter, InteractionInput Input);

    private static readonly JsonSerializerOptions s_indentedJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

#pragma warning restore ASPIREINTERACTION001
