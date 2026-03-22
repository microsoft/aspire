// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.OpenCode;

/// <summary>
/// Scans for OpenCode environments and provides an applicator to configure the Aspire MCP server.
/// </summary>
internal sealed class OpenCodeAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private const string OpenCodeConfigFileName = "opencode.jsonc";
    private const string AspireServerName = "aspire";
    private static readonly string s_skillFilePath = Path.Combine(".opencode", "skill", CommonAgentApplicators.AspireSkillName, "SKILL.md");
    private static readonly string s_skillBaseDirectory = Path.Combine(".opencode", "skill");
    private const string SkillFileDescription = "Create Aspire skill file (.opencode/skill/aspire/SKILL.md)";

    private readonly IOpenCodeCliRunner _openCodeCliRunner;
    private readonly PlaywrightCliInstaller _playwrightCliInstaller;
    private readonly ILogger<OpenCodeAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="OpenCodeAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="openCodeCliRunner">The OpenCode CLI runner for checking if OpenCode is installed.</param>
    /// <param name="playwrightCliInstaller">The Playwright CLI installer for secure installation.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public OpenCodeAgentEnvironmentScanner(IOpenCodeCliRunner openCodeCliRunner, PlaywrightCliInstaller playwrightCliInstaller, ILogger<OpenCodeAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(openCodeCliRunner);
        ArgumentNullException.ThrowIfNull(playwrightCliInstaller);
        ArgumentNullException.ThrowIfNull(logger);
        _openCodeCliRunner = openCodeCliRunner;
        _playwrightCliInstaller = playwrightCliInstaller;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting OpenCode environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);
        _logger.LogDebug("Workspace root: {RepositoryRoot}", context.RepositoryRoot.FullName);

        // Look for existing opencode.jsonc file at workspace root
        var configDirectory = context.RepositoryRoot;
        var configFilePath = Path.Combine(configDirectory.FullName, OpenCodeConfigFileName);
        var configFileExists = File.Exists(configFilePath);

        if (configFileExists)
        {
            _logger.LogDebug("Found existing opencode.jsonc at: {ConfigFilePath}", configFilePath);
            
            // Check if aspire is already configured
            _logger.LogDebug("Checking if Aspire MCP server is already configured in opencode.jsonc...");
            if (!HasAspireServerConfigured(configFilePath))
            {
                // Config file exists but aspire is not configured - offer to add it
                _logger.LogDebug("Adding OpenCode applicator to update existing opencode.jsonc");
                context.AddApplicator(CreateApplicator(configDirectory));
            }
            else
            {
                _logger.LogDebug("Aspire MCP server is already configured");
            }

            // Register Playwright CLI installation applicator
            CommonAgentApplicators.AddPlaywrightCliApplicator(context, _playwrightCliInstaller, s_skillBaseDirectory);

            // Try to add skill file applicator for OpenCode
            CommonAgentApplicators.TryAddSkillFileApplicator(
                context,
                context.RepositoryRoot,
                s_skillFilePath,
                SkillFileDescription);
        }
        else
        {
            // No config file - check if OpenCode CLI is installed
            _logger.LogDebug("No opencode.jsonc found, checking for OpenCode CLI installation...");
            var openCodeVersion = await _openCodeCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);

            if (openCodeVersion is not null)
            {
                _logger.LogDebug("Found OpenCode CLI version: {Version}", openCodeVersion);
                // OpenCode is installed - offer to create config
                _logger.LogDebug("Adding OpenCode applicator to create new opencode.jsonc at: {ConfigDirectory}", configDirectory.FullName);
                context.AddApplicator(CreateApplicator(configDirectory));
                
                // Register Playwright CLI installation applicator
                CommonAgentApplicators.AddPlaywrightCliApplicator(context, _playwrightCliInstaller, s_skillBaseDirectory);
                
                // Try to add skill file applicator for OpenCode
                CommonAgentApplicators.TryAddSkillFileApplicator(
                    context,
                    context.RepositoryRoot,
                    s_skillFilePath,
                    SkillFileDescription);
            }
            else
            {
                _logger.LogDebug("OpenCode CLI not found - skipping");
            }
        }
    }

    /// <summary>
    /// Checks if the opencode.jsonc file has an "aspire" server configured.
    /// </summary>
    /// <param name="configFilePath">The path to the opencode.jsonc file.</param>
    /// <returns>True if the aspire server is already configured, false otherwise.</returns>
    private static bool HasAspireServerConfigured(string configFilePath)
    {
        try
        {
            var content = File.ReadAllText(configFilePath);

            // Remove single-line comments for parsing (JSONC support)
            content = RemoveJsonComments(content);

            var config = JsonNode.Parse(content)?.AsObject();

            if (config is null)
            {
                return false;
            }

            if (config.TryGetPropertyValue("mcp", out var mcpNode) && mcpNode is JsonObject mcp)
            {
                return mcp.ContainsKey(AspireServerName);
            }

            return false;
        }
        catch (JsonException)
        {
            // If the JSON is malformed, assume aspire is not configured
            return false;
        }
    }

    /// <summary>
    /// Removes single-line comments from JSONC content.
    /// </summary>
    private static string RemoveJsonComments(string jsonc)
    {
        var result = new System.Text.StringBuilder();
        var lines = jsonc.Split('\n');

        foreach (var line in lines)
        {
            var trimmedLine = line;
            var commentIndex = line.IndexOf("//", StringComparison.Ordinal);

            // Simple heuristic: if // appears and it's not inside a string, remove it
            // This is a simplified approach - a full JSONC parser would be more robust
            if (commentIndex >= 0)
            {
                // Count quotes before the comment to check if we're in a string
                var beforeComment = line[..commentIndex];
                var quoteCount = beforeComment.Count(c => c == '"');

                // If even number of quotes, we're not in a string
                if (quoteCount % 2 == 0)
                {
                    trimmedLine = beforeComment;
                }
            }

            result.AppendLine(trimmedLine);
        }

        return result.ToString();
    }

    /// <summary>
    /// Creates an applicator for configuring the MCP server in the opencode.jsonc file.
    /// </summary>
    private static AgentEnvironmentApplicator CreateApplicator(DirectoryInfo configDirectory)
    {
        return new AgentEnvironmentApplicator(
            OpenCodeAgentEnvironmentScannerStrings.ApplicatorDescription,
            async cancellationToken => await ApplyMcpConfigurationAsync(
                configDirectory,
                cancellationToken));
    }

    /// <summary>
    /// Creates or updates the opencode.jsonc file with the Aspire MCP server configuration.
    /// </summary>
    private static async Task ApplyMcpConfigurationAsync(
        DirectoryInfo configDirectory,
        CancellationToken cancellationToken)
    {
        var configFilePath = Path.Combine(configDirectory.FullName, OpenCodeConfigFileName);
        var config = await McpConfigFileHelper.ReadConfigAsync(configFilePath, cancellationToken, RemoveJsonComments);

        // Ensure schema is set for new configs
        if (!config.ContainsKey("$schema"))
        {
            config["$schema"] = "https://opencode.ai/config.json";
        }

        // Ensure "mcp" object exists
        if (!config.ContainsKey("mcp") || config["mcp"] is not JsonObject)
        {
            config["mcp"] = new JsonObject();
        }

        var mcp = config["mcp"]!.AsObject();

        // Add the "aspire" server configuration
        mcp[AspireServerName] = new JsonObject
        {
            ["type"] = "local",
            ["command"] = new JsonArray("aspire", "agent", "mcp"),
            ["enabled"] = true
        };

        // Write the updated config using AOT-compatible serialization
        var jsonOutput = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);
        await File.WriteAllTextAsync(configFilePath, jsonOutput, cancellationToken);
    }

}
