// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Provides shared methods for reading and parsing MCP configuration files across agent environment scanners.
/// </summary>
internal static class McpConfigFileHelper
{
    /// <summary>
    /// Checks if a specific server is configured in an MCP config file under the given container key.
    /// </summary>
    /// <param name="configFilePath">The path to the MCP configuration file.</param>
    /// <param name="serverContainerKey">The JSON property name that holds the servers object (e.g., "servers", "mcpServers", "mcp").</param>
    /// <param name="serverName">The name of the server to check for.</param>
    /// <param name="preprocessContent">Optional function to preprocess file content before parsing (e.g., to strip JSONC comments).</param>
    /// <returns><c>true</c> if the server is configured; <c>false</c> otherwise (including when the file is missing or malformed).</returns>
    public static bool HasServerConfigured(string configFilePath, string serverContainerKey, string serverName, Func<string, string>? preprocessContent = null)
    {
        if (!File.Exists(configFilePath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(configFilePath);

            if (preprocessContent is not null)
            {
                content = preprocessContent(content);
            }

            var config = JsonNode.Parse(content)?.AsObject();

            if (config is null)
            {
                return false;
            }

            if (config.TryGetPropertyValue(serverContainerKey, out var serversNode) && serversNode is JsonObject servers)
            {
                return servers.ContainsKey(serverName);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads an existing MCP config file and parses it into a <see cref="JsonObject"/>, or creates a new one if the file doesn't exist.
    /// </summary>
    /// <param name="configFilePath">The path to the MCP configuration file.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <param name="preprocessContent">Optional function to preprocess file content before parsing (e.g., to strip JSONC comments).</param>
    /// <returns>The parsed <see cref="JsonObject"/> from the file, or a new empty <see cref="JsonObject"/> if the file doesn't exist.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the file exists but contains malformed JSON, wrapping the underlying <see cref="JsonException"/>.</exception>
    public static async Task<JsonObject> ReadConfigAsync(string configFilePath, CancellationToken cancellationToken, Func<string, string>? preprocessContent = null)
    {
        if (!File.Exists(configFilePath))
        {
            return new JsonObject();
        }

        var content = await File.ReadAllTextAsync(configFilePath, cancellationToken);

        if (preprocessContent is not null)
        {
            content = preprocessContent(content);
        }

        try
        {
            return JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.MalformedConfigFileError, configFilePath), ex);
        }
    }
}
