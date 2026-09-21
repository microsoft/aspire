// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Conservative MCP entry creation and selected-target repair of the old "aspire mcp start" command.
/// </summary>
internal static class McpConfiguration
{
    internal const string ServerName = "aspire";

    public static AgentConfigurationEdit Apply(
        JsonObject root,
        string containerName,
        bool commandArray,
        string type,
        bool copilot,
        bool bare = false)
    {
        var servers = bare ? root : AgentConfigurationJson.Object(root, containerName);
        if (!servers.TryGetPropertyValue(ServerName, out var node))
        {
            var server = new JsonObject { ["type"] = type };
            if (commandArray)
            {
                server["command"] = new JsonArray("aspire", "agent", "mcp");
            }
            else
            {
                server["command"] = "aspire";
                server["args"] = new JsonArray("agent", "mcp");
            }

            if (copilot)
            {
                // Copilot does not inherit arbitrary environment variables for local MCP
                // servers. Preserve the existing Aspire DOTNET_ROOT pass-through contract.
                server["env"] = new JsonObject { ["DOTNET_ROOT"] = "${DOTNET_ROOT}" };
                server["tools"] = new JsonArray("*");
            }

            servers[ServerName] = server;
            return AgentConfigurationEdit.Applied(AgentConfigurationStrings.McpConfigured);
        }

        if (node is JsonValue disabledValue && disabledValue.TryGetValue<bool>(out var value) && !value)
        {
            return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.Disabled);
        }

        if (node is not JsonObject existing)
        {
            throw AgentConfigurationJson.Shape($"{containerName}.{ServerName}");
        }

        if (AgentConfigurationJson.Boolean(existing, "enabled") is false ||
            AgentConfigurationJson.Boolean(existing, "disabled") is true)
        {
            return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.Disabled);
        }

        if (existing.TryGetPropertyValue("type", out var typeNode) &&
            AgentConfigurationJson.String(typeNode) is not ("stdio" or "local"))
        {
            return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.Conflict);
        }

        JsonArray arguments;
        var offset = 0;
        if (commandArray)
        {
            arguments = AgentConfigurationJson.OptionalStrings(existing, "command")
                ?? throw AgentConfigurationJson.Shape("command");
            if (arguments.Count == 0 || !IsAspireExecutable(AgentConfigurationJson.String(arguments[0])))
            {
                return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.Conflict);
            }

            offset = 1;
        }
        else
        {
            if (!IsAspireExecutable(AgentConfigurationJson.String(existing["command"])))
            {
                return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.Conflict);
            }

            arguments = AgentConfigurationJson.OptionalStrings(existing, "args")
                ?? throw AgentConfigurationJson.Shape("args");
        }

        // Accepted raw forms are ["mcp", "start", ...] and ["agent", "mcp", ...].
        // OpenCode prefixes the same array with the executable. Preserve all trailing
        // arguments, custom executable paths, environment, tool filters and other fields.
        if (arguments.Count >= offset + 2 &&
            AgentConfigurationJson.String(arguments[offset]) == "mcp" &&
            AgentConfigurationJson.String(arguments[offset + 1]) == "start")
        {
            arguments[offset] = "agent";
            arguments[offset + 1] = "mcp";
        }
        else if (arguments.Count < offset + 2 ||
                 AgentConfigurationJson.String(arguments[offset]) != "agent" ||
                 AgentConfigurationJson.String(arguments[offset + 1]) != "mcp")
        {
            return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.Conflict);
        }

        return AgentConfigurationEdit.Applied(AgentConfigurationStrings.McpConfigured);
    }

    public static AgentConfigurationEdit? CheckPolicy(IEnumerable<JsonObject> settings)
        => CheckPolicy(settings, [], managedAllowlistOnly: false);

    public static AgentConfigurationEdit? CheckPolicy(
        IEnumerable<JsonObject> settings,
        IReadOnlyList<JsonObject> managedSettings,
        bool managedAllowlistOnly)
    {
        var allSettings = settings.ToArray();
        foreach (var root in allSettings)
        {
            foreach (var key in new[] { "disabledMcpServers", "disabledMcpjsonServers" })
            {
                if (AgentConfigurationJson.OptionalStrings(root, key)?.Any(value => AgentConfigurationJson.String(value) == ServerName) is true)
                {
                    return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.Disabled);
                }
            }

            if (ReadPolicyEntries(root, "deniedMcpServers") is { } denied &&
                denied.Any(entry => !IsNameOnlyPolicy(entry) || MatchesName(entry)))
            {
                return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.PolicyBlocked);
            }
        }

        // Claude allowlists merge, unless managed policy restricts them to managed sources.
        // Copilot's allowlist is managed-only. Denylists above still apply from every scope.
        // https://code.claude.com/docs/en/settings-reference#allowmanagedmcpserversonly
        var onlyManaged = managedAllowlistOnly ||
            managedSettings.Any(root => AgentConfigurationJson.Boolean(root, "allowManagedMcpServersOnly") is true);
        var allowlists = (onlyManaged ? managedSettings : allSettings)
            .Select(root => ReadPolicyEntries(root, "allowedMcpServers")).OfType<JsonArray>().ToArray();
        if (allowlists.Length > 0 && !allowlists.SelectMany(entries => entries).Any(entry => IsNameOnlyPolicy(entry) && MatchesName(entry)))
        {
            // Command/URL patterns and dynamic matcher forms cannot be safely evaluated
            // without the client's policy engine. Leave them intact and report the limitation.
            return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.PolicyBlocked);
        }

        return null;
    }

    public static bool IsDefaultEntry(JsonObject server, bool commandArray)
    {
        if (server.Any(property => property.Key is not ("type" or "command" or "args" or "env" or "tools" or "enabled" or "disabled")))
        {
            return false;
        }

        var command = commandArray ? AgentConfigurationJson.OptionalStrings(server, "command") : AgentConfigurationJson.OptionalStrings(server, "args");
        string[] expected = commandArray ? ["aspire", "agent", "mcp"] : ["agent", "mcp"];
        if (command is null || !command.Select(AgentConfigurationJson.String).SequenceEqual(expected, StringComparer.Ordinal) ||
            (!commandArray && AgentConfigurationJson.String(server["command"]) != "aspire"))
        {
            return false;
        }

        if (server.TryGetPropertyValue("env", out var environment) &&
            !JsonNode.DeepEquals(environment, new JsonObject { ["DOTNET_ROOT"] = "${DOTNET_ROOT}" }))
        {
            return false;
        }

        return !server.TryGetPropertyValue("tools", out var tools) || JsonNode.DeepEquals(tools, new JsonArray("*"));
    }

    public static bool UsesBareServers(JsonObject root)
        => !root.ContainsKey("mcpServers") && root.Any(property => property.Key != "$schema") &&
            root.Where(property => property.Key != "$schema").All(property =>
                (property.Value is JsonObject server && (server.ContainsKey("command") || server.ContainsKey("url") ||
                    server.ContainsKey("disabled") || server.ContainsKey("enabled"))) ||
                (property.Value is JsonValue value && value.TryGetValue<bool>(out var enabled) && !enabled));

    private static bool IsAspireExecutable(string? command)
    {
        if (command is null)
        {
            return false;
        }

        var name = command.Split(['/', '\\']).Last();
        return name.Equals("aspire", StringComparison.OrdinalIgnoreCase) || name.Equals("aspire.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray? ReadPolicyEntries(JsonObject root, string key)
        => !root.TryGetPropertyValue(key, out var node) ? null : node as JsonArray ?? throw AgentConfigurationJson.Shape(key);

    private static bool IsNameOnlyPolicy(JsonNode? node)
        => node is JsonObject entry && entry.Count == 1 && AgentConfigurationJson.String(entry["serverName"]) is not null;

    private static bool MatchesName(JsonNode? node)
        => node is JsonObject entry && AgentConfigurationJson.String(entry["serverName"]) == ServerName;
}
