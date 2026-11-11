// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks for outdated agent configuration that 'aspire agent init' updates, such as deprecated
/// 'mcp start' commands and copied Aspire skills that can shadow configured skill sources.
/// </summary>
internal sealed class DeprecatedAgentConfigCheck : IEnvironmentCheck
{
    internal const string AspireSkillsCheckName = "aspire-skill-sources";

    private readonly IGitRepository _gitRepository;
    private readonly CliExecutionContext _executionContext;

    internal static string GetCheckName(string agentName) => $"agent-config-{agentName.ToLowerInvariant().Replace(" ", "-")}";

    // Define the agent config locations and their detection patterns
    private static readonly AgentConfigLocation[] s_configLocations =
    [
        new AgentConfigLocation("Claude Code", ".mcp.json", ConfigFormat.McpServersWithArgs, "mcpServers"),
        new AgentConfigLocation("VS Code", ".vscode/mcp.json", ConfigFormat.McpServersWithArgs, "servers"),
        new AgentConfigLocation("Copilot CLI", ".github/copilot/mcp.json", ConfigFormat.McpServersWithArgs, "servers"),
        new AgentConfigLocation("OpenCode", "opencode.jsonc", ConfigFormat.McpWithCommandArray, "mcp"),
    ];

    public DeprecatedAgentConfigCheck(
        IGitRepository gitRepository,
        CliExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(gitRepository);
        ArgumentNullException.ThrowIfNull(executionContext);
        _gitRepository = gitRepository;
        _executionContext = executionContext;
    }

    /// <inheritdoc />
    public int Order => 100; // Run after core checks

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<EnvironmentCheckResult>();
        var workingDirectory = _executionContext.WorkingDirectory;

        foreach (var location in s_configLocations)
        {
            var configPath = Path.Combine(workingDirectory.FullName, location.RelativePath);

            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(configPath);
                var config = JsonNode.Parse(content)?.AsObject();

                if (config is null)
                {
                    continue;
                }

                if (HasDeprecatedMcpCommand(config, location))
                {
                    results.Add(new EnvironmentCheckResult
                    {
                        Category = EnvironmentCheckCategories.Environment,
                        Name = GetCheckName(location.AgentName),
                        Status = EnvironmentCheckStatus.Warning,
                        Message = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.DeprecatedConfigWarning, location.AgentName),
                        Fix = AgentCommandStrings.DeprecatedConfigFix
                    });
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON files
            }
            catch (IOException)
            {
                // Skip files that can't be read
            }
        }

        await AddAspireSkillsResultsAsync(results, cancellationToken);
        return results;
    }

    /// <summary>
    /// Adds warnings for copied Aspire skills that can shadow configured skill sources.
    /// </summary>
    private async Task AddAspireSkillsResultsAsync(List<EnvironmentCheckResult> results, CancellationToken cancellationToken)
    {
        var workingDirectory = _executionContext.WorkingDirectory;
        var workspaceRoot = await _gitRepository.GetRootAsync(cancellationToken) ?? workingDirectory;
        var skillCopies = LocalAspireSkills.Find(workingDirectory, workspaceRoot, _executionContext.HomeDirectory, cancellationToken);
        if (skillCopies.Count > 0)
        {
            var displayPaths = skillCopies
                .Select(copy => AgentPath.GetDisplayPath(copy.Directory, workspaceRoot, _executionContext))
                .Order(StringComparer.Ordinal)
                .ToArray();
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.AppHost,
                Name = AspireSkillsCheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocalSkillsConflict, string.Empty).TrimEnd(),
                Fix = DoctorCommandStrings.AspireSkillsFix,
                Files = displayPaths,
                Metadata = new JsonObject
                {
                    ["workspaceRoot"] = workspaceRoot.FullName
                }
            });
        }
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
