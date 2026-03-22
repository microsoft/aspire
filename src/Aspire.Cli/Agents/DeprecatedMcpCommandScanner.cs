// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents;

/// <summary>
/// Scans for deprecated 'mcp start' command usage in agent environment configurations
/// and offers to update them to use the new 'agent mcp' command.
/// </summary>
internal sealed class DeprecatedMcpCommandScanner : IAgentEnvironmentScanner
{
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<DeprecatedMcpCommandScanner> _logger;

    // Define the agent config locations and their detection patterns
    private static readonly AgentConfigLocation[] s_configLocations =
    [
        new AgentConfigLocation("Claude Code", ".mcp.json", ConfigFormat.McpServersWithArgs, "mcpServers"),
        new AgentConfigLocation("VS Code", ".vscode/mcp.json", ConfigFormat.McpServersWithArgs, "servers"),
        new AgentConfigLocation("Copilot CLI", ".github/copilot/mcp.json", ConfigFormat.McpServersWithArgs, "servers"),
        new AgentConfigLocation("OpenCode", "opencode.jsonc", ConfigFormat.McpWithCommandArray, "mcp"),
    ];

    public DeprecatedMcpCommandScanner(CliExecutionContext executionContext, ILogger<DeprecatedMcpCommandScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);

        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Scanning for deprecated MCP command usage in agent configurations");

        // Debug: Write scanning info to temp file for diagnostics
        var debugPath = Path.Combine(Path.GetTempPath(), "aspire-deprecated-scan.log");
        File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}] Scanning context.RepositoryRoot: {context.RepositoryRoot.FullName}\n");

        foreach (var location in s_configLocations)
        {
            var configPath = Path.Combine(context.RepositoryRoot.FullName, location.RelativePath);
            File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}] Checking {location.AgentName}: {configPath}\n");

            if (!File.Exists(configPath))
            {
                File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}]   -> File does not exist\n");
                continue;
            }

            File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}]   -> File EXISTS!\n");

            try
            {
                var content = File.ReadAllText(configPath);
                File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}]   -> Content: {content.Substring(0, Math.Min(200, content.Length))}\n");
                var config = JsonNode.Parse(content)?.AsObject();

                if (config is null)
                {
                    File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}]   -> JSON parse returned null\n");
                    continue;
                }

                var hasDeprecated = HasDeprecatedMcpCommand(config, location);
                File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}]   -> HasDeprecatedMcpCommand: {hasDeprecated}\n");

                if (hasDeprecated)
                {
                    _logger.LogDebug("Found deprecated MCP command in {AgentName} config at {Path}", location.AgentName, configPath);

                    // Create an applicator to update this config
                    var applicator = CreateUpdateApplicator(location, configPath, config);
                    context.AddApplicator(applicator);
                    File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}]   -> Added applicator\n");
                }
            }
            catch (JsonException ex)
            {
                File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}]   -> JsonException: {ex.Message}\n");
                _logger.LogDebug(ex, "Failed to parse {AgentName} config at {Path}", location.AgentName, configPath);
            }
            catch (IOException ex)
            {
                File.AppendAllText(debugPath, $"[{DateTime.UtcNow:O}]   -> IOException: {ex.Message}\n");
                _logger.LogDebug(ex, "Failed to read {AgentName} config at {Path}", location.AgentName, configPath);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks for deprecated MCP command patterns in the config.
    /// </summary>
    private static bool HasDeprecatedMcpCommand(JsonObject config, AgentConfigLocation location)
    {
        return location.Format switch
        {
            ConfigFormat.McpServersWithArgs => HasDeprecatedMcpServersArgs(config, location.ServersKey),
            ConfigFormat.McpWithCommandArray => HasDeprecatedMcpCommandArray(config, location.ServersKey),
            _ => false
        };
    }

    /// <summary>
    /// Checks for deprecated pattern: {serversKey}.aspire.args = ["mcp", "start"]
    /// Used by Claude Code (mcpServers), VS Code (servers), Copilot CLI (servers)
    /// </summary>
    private static bool HasDeprecatedMcpServersArgs(JsonObject config, string serversKey)
    {
        if (!config.TryGetPropertyValue(serversKey, out var serversNode) || serversNode is not JsonObject servers)
        {
            return false;
        }

        if (!servers.TryGetPropertyValue("aspire", out var aspireNode) || aspireNode is not JsonObject aspire)
        {
            return false;
        }

        if (!aspire.TryGetPropertyValue("args", out var argsNode) || argsNode is not JsonArray args)
        {
            return false;
        }

        // Check if args is ["mcp", "start"]
        if (args.Count >= 2 &&
            args[0]?.GetValue<string>() == "mcp" &&
            args[1]?.GetValue<string>() == "start")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks for deprecated pattern: {serversKey}.aspire.command = ["aspire", "mcp", "start"]
    /// Used by OpenCode
    /// </summary>
    private static bool HasDeprecatedMcpCommandArray(JsonObject config, string serversKey)
    {
        if (!config.TryGetPropertyValue(serversKey, out var mcpNode) || mcpNode is not JsonObject mcp)
        {
            return false;
        }

        if (!mcp.TryGetPropertyValue("aspire", out var aspireNode) || aspireNode is not JsonObject aspire)
        {
            return false;
        }

        if (!aspire.TryGetPropertyValue("command", out var commandNode) || commandNode is not JsonArray command)
        {
            return false;
        }

        // Check if command is ["aspire", "mcp", "start"]
        if (command.Count >= 3 &&
            command[0]?.GetValue<string>() == "aspire" &&
            command[1]?.GetValue<string>() == "mcp" &&
            command[2]?.GetValue<string>() == "start")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates an applicator that updates the config to use the new command format.
    /// </summary>
    private AgentEnvironmentApplicator CreateUpdateApplicator(AgentConfigLocation location, string configPath, JsonObject config)
    {
        var description = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.DeprecatedConfigUpdate_Description, location.AgentName);

        return new AgentEnvironmentApplicator(
            description,
            async cancellationToken =>
            {
                await UpdateConfigAsync(location, configPath, config, cancellationToken);
            },
            promptGroup: McpInitPromptGroup.ConfigUpdates);
    }

    /// <summary>
    /// Updates the config file to use the new command format.
    /// </summary>
    private async Task UpdateConfigAsync(AgentConfigLocation location, string configPath, JsonObject config, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Updating {AgentName} config at {Path}", location.AgentName, configPath);

        switch (location.Format)
        {
            case ConfigFormat.McpServersWithArgs:
                UpdateMcpServersArgs(config, location.ServersKey);
                break;

            case ConfigFormat.McpWithCommandArray:
                UpdateMcpCommandArray(config, location.ServersKey);
                break;
        }

        var jsonContent = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);
        await File.WriteAllTextAsync(configPath, jsonContent, cancellationToken);
    }

    /// <summary>
    /// Updates {serversKey}.aspire.args from ["mcp", "start"] to ["agent", "mcp"]
    /// </summary>
    private static void UpdateMcpServersArgs(JsonObject config, string serversKey)
    {
        if (config.TryGetPropertyValue(serversKey, out var serversNode) &&
            serversNode is JsonObject servers &&
            servers.TryGetPropertyValue("aspire", out var aspireNode) &&
            aspireNode is JsonObject aspire)
        {
            aspire["args"] = new JsonArray("agent", "mcp");
        }
    }

    /// <summary>
    /// Updates {serversKey}.aspire.command from ["aspire", "mcp", "start"] to ["aspire", "agent", "mcp"]
    /// </summary>
    private static void UpdateMcpCommandArray(JsonObject config, string serversKey)
    {
        if (config.TryGetPropertyValue(serversKey, out var mcpNode) &&
            mcpNode is JsonObject mcp &&
            mcp.TryGetPropertyValue("aspire", out var aspireNode) &&
            aspireNode is JsonObject aspire)
        {
            aspire["command"] = new JsonArray("aspire", "agent", "mcp");
        }
    }

    /// <summary>
    /// Represents a known agent configuration file location.
    /// </summary>
    private sealed record AgentConfigLocation(string AgentName, string RelativePath, ConfigFormat Format, string ServersKey);

    /// <summary>
    /// Configuration file format for detecting deprecated commands.
    /// </summary>
    private enum ConfigFormat
    {
        /// <summary>
        /// Format: {serversKey}.aspire.args = ["mcp", "start"]
        /// </summary>
        McpServersWithArgs,

        /// <summary>
        /// Format: {serversKey}.aspire.command = ["aspire", "mcp", "start"]
        /// </summary>
        McpWithCommandArray
    }
}
