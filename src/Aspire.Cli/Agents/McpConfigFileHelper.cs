// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Provides shared methods for reading and updating selected MCP configuration files.
/// </summary>
internal static class McpConfigFileHelper
{
    public static AgentFileEdit CreateEdit(
        IAgentClient client,
        string configFilePath,
        Configuration configuration,
        bool configureServer,
        AgentConfigurationScope scope)
    {
        return new(client, configFilePath,
            bytes => ParseConfig(bytes, configuration.JsonDocumentOptions, configFilePath),
            (config, _, _) =>
            {
                var repaired = UpdateDeprecatedCommand(config, configuration.ServersPropertyName);
                var configured = configureServer && ConfigureMcpServer(config, configuration, scope, configFilePath);
                return Task.FromResult(AgentConfigurationEdit.Applied() with { Repaired = repaired, Added = configured });
            });
    }

    internal sealed record Configuration
    {
        public required string ServersPropertyName { get; init; }
        public JsonDocumentOptions JsonDocumentOptions { get; init; }
        public string? Schema { get; init; }
        public JsonObject ServerConfiguration { get; init; } = new()
        {
            ["command"] = "aspire",
            ["args"] = new JsonArray("agent", "mcp")
        };
        public JsonObject? GlobalServerConfiguration { get; init; }
    }

    public static JsonObject ParseConfig(ReadOnlyMemory<byte> content, JsonDocumentOptions documentOptions, string configFilePath)
    {
        JsonNode? root;
        try
        {
            // Preserve File.ReadAllTextAsync's BOM-aware decoding (including UTF-16/32)
            // while parsing the exact bytes captured for publication-time revalidation.
            using var stream = new MemoryStream(content.ToArray());
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            root = JsonNode.Parse(reader.ReadToEnd(), documentOptions: documentOptions);
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
        Configuration configuration,
        AgentConfigurationScope scope,
        string configFilePath)
    {
        var servers = GetServerContainer(config, configuration.ServersPropertyName, configFilePath);
        if (servers?.ContainsKey("aspire") is true)
        {
            return false;
        }

        if (configuration.Schema is { } schema && !config.ContainsKey("$schema"))
        {
            config["$schema"] = schema;
        }

        if (servers is null)
        {
            servers = new JsonObject();
            config[configuration.ServersPropertyName] = servers;
        }

        var serverConfiguration = scope is AgentConfigurationScope.Global
            ? configuration.GlobalServerConfiguration ?? configuration.ServerConfiguration
            : configuration.ServerConfiguration;
        // JsonNodes have single-parent ownership; each destination needs its own copy.
        servers["aspire"] = serverConfiguration.DeepClone();

        return true;
    }

    public static JsonObject? GetServerContainer(JsonObject config, string serverContainerKey, string configFilePath)
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
