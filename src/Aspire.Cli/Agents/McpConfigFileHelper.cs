// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Provides shared methods for reading and updating selected MCP configuration files.
/// </summary>
internal static class McpConfigFileHelper
{
    public static async Task<(JsonObject Config, string Path)> ReadSelectedConfigAsync(
        string configFilePath,
        AgentClient client,
        AgentConfigurationScope scope,
        IEnvironment environment,
        CancellationToken cancellationToken)
    {
        var config = await ReadConfigAsync(configFilePath, client.McpJsonDocumentOptions, cancellationToken);
        if (client == AgentClient.OpenCode &&
            Path.GetFileName(configFilePath) == "opencode.jsonc" &&
            (scope is AgentConfigurationScope.Project ||
                environment.GetEnvironmentVariable("OPENCODE_CONFIG") is not { Length: > 0 }))
        {
            var servers = GetServerContainer(config, client.McpServersPropertyName, configFilePath);
            // Remote servers use their URL instead of an inherited local command.
            if (servers is not null &&
                servers.TryGetPropertyValue("aspire", out var server) &&
                (server is not JsonObject aspire ||
                    aspire.ContainsKey("command") ||
                    (aspire["type"] is JsonValue type &&
                        type.TryGetValue<string>(out var connectionType) &&
                        connectionType == "remote")))
            {
                return (config, configFilePath);
            }

            // OpenCode merges JSON before JSONC, so JSONC {"mcp":{"aspire":{"enabled":true}}}
            // inherits its command from opencode.json. Repair the command in its owning file
            // without shadowing pins or rewriting the higher-precedence settings.
            // Explicit OPENCODE_CONFIG files do not use sibling discovery.
            // https://opencode.ai/docs/config/#locations
            var jsonPath = Path.ChangeExtension(configFilePath, ".json");
            var jsonConfig = await ReadConfigAsync(jsonPath, client.McpJsonDocumentOptions, cancellationToken);
            if (GetServerContainer(jsonConfig, client.McpServersPropertyName, jsonPath)?.ContainsKey("aspire") is true)
            {
                return (jsonConfig, jsonPath);
            }
        }

        return (config, configFilePath);
    }

    /// <summary>
    /// Reads an existing MCP config file and parses it into a <see cref="JsonObject"/>, or creates a new one if the file doesn't exist.
    /// </summary>
    /// <param name="configFilePath">The path to the MCP configuration file.</param>
    /// <param name="documentOptions">The JSON parsing options for this configuration format.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The parsed <see cref="JsonObject"/> from the file, or a new empty <see cref="JsonObject"/> if the file doesn't exist.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the file contains malformed JSON or its root is not an object.
    /// Malformed JSON exceptions wrap the underlying <see cref="JsonException"/>.
    /// </exception>
    private static async Task<JsonObject> ReadConfigAsync(string configFilePath, JsonDocumentOptions documentOptions, CancellationToken cancellationToken)
    {
        if (!File.Exists(configFilePath))
        {
            return new JsonObject();
        }

        var content = await File.ReadAllTextAsync(configFilePath, cancellationToken);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content, documentOptions: documentOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.MalformedConfigFileError, configFilePath), ex);
        }

        return root as JsonObject
            ?? throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfigurationFileMustBeJsonObject, configFilePath));
    }

    public static bool ConfigureMcpServer(
        JsonObject config,
        AgentClient client,
        AgentConfigurationScope scope,
        string configFilePath)
    {
        var servers = GetServerContainer(config, client.McpServersPropertyName, configFilePath);
        if (servers?.ContainsKey("aspire") is true)
        {
            return false;
        }

        if (client.McpSchema is { } schema && !config.ContainsKey("$schema"))
        {
            config["$schema"] = schema;
        }

        if (servers is null)
        {
            servers = new JsonObject();
            config[client.McpServersPropertyName] = servers;
        }

        var serverConfiguration = scope is AgentConfigurationScope.Global
            ? client.GlobalMcpServerConfiguration ?? client.McpServerConfiguration
            : client.McpServerConfiguration;
        // JsonNodes have single-parent ownership; each destination needs its own copy.
        servers["aspire"] = serverConfiguration.DeepClone();

        return true;
    }

    private static JsonObject? GetServerContainer(JsonObject config, string serverContainerKey, string configFilePath)
    {
        var serverContainer = config[serverContainerKey];
        if (serverContainer is not null && serverContainer is not JsonObject)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                AgentCommandStrings.McpServerContainerMustBeJsonObject, serverContainerKey, configFilePath));
        }

        return serverContainer as JsonObject;
    }

    public static bool UpdateDeprecatedCommand(JsonObject config, string serverContainerKey)
    {
        if (config[serverContainerKey] is not JsonObject servers || servers["aspire"] is not JsonObject aspire)
        {
            return false;
        }

        // Legacy MCP entries use command: "aspire", args: ["mcp", "start"], or OpenCode's
        // command: ["aspire", "mcp", "start"]. Only these exact deprecated forms are repaired;
        // custom commands and version-pinned server definitions are left intact.
        if (MatchesArgument(aspire["command"], "aspire") &&
            aspire["args"] is JsonArray { Count: 2 } args &&
            MatchesArgument(args[0], "mcp") && MatchesArgument(args[1], "start"))
        {
            aspire["args"] = new JsonArray("agent", "mcp");
        }
        else if (aspire["command"] is JsonArray { Count: 3 } command &&
            MatchesArgument(command[0], "aspire") &&
            MatchesArgument(command[1], "mcp") && MatchesArgument(command[2], "start"))
        {
            aspire["command"] = new JsonArray("aspire", "agent", "mcp");
        }
        else
        {
            return false;
        }

        return true;
    }

    private static bool MatchesArgument(JsonNode? argument, string expected)
    {
        return argument is JsonValue value && value.TryGetValue<string>(out var text) && text == expected;
    }
}
