// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.ClaudeCode;

/// <summary>
/// Scans for Claude Code environments and provides an applicator to configure the Aspire MCP server.
/// </summary>
internal sealed class ClaudeCodeAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private const string ClaudeCodeFolderName = ".claude";
    private const string McpConfigFileName = ".mcp.json";
    private const string AspireServerName = "aspire";

    private readonly IClaudeCodeCliRunner _claudeCodeCliRunner;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<ClaudeCodeAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ClaudeCodeAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="claudeCodeCliRunner">The Claude Code CLI runner for checking if Claude Code is installed.</param>
    /// <param name="executionContext">The CLI execution context for accessing environment variables and settings.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public ClaudeCodeAgentEnvironmentScanner(IClaudeCodeCliRunner claudeCodeCliRunner, CliExecutionContext executionContext, ILogger<ClaudeCodeAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(claudeCodeCliRunner);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);
        _claudeCodeCliRunner = claudeCodeCliRunner;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting Claude Code environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);
        _logger.LogDebug("Workspace root: {RepositoryRoot}", context.RepositoryRoot.FullName);

        // Find the .claude folder to determine if Claude Code is being used in this project
        _logger.LogDebug("Searching for .claude folder...");
        var claudeCodeFolder = FindClaudeCodeFolder(context.WorkingDirectory, context.RepositoryRoot);

        if (claudeCodeFolder is not null)
        {
            // If .claude folder is found, override the workspace root with its parent directory
            var workspaceRoot = claudeCodeFolder.Parent ?? context.RepositoryRoot;
            _logger.LogDebug("Inferred workspace root from .claude folder parent: {WorkspaceRoot}", workspaceRoot.FullName);

            // Check if the aspire server is already configured in .mcp.json
            _logger.LogDebug("Checking if Aspire MCP server is already configured in .mcp.json...");
            if (!HasAspireServerConfigured(workspaceRoot))
            {
                // Found a .claude folder - add an applicator to configure MCP
                _logger.LogDebug("Adding Claude Code applicator for .mcp.json at: {WorkspaceRoot}", workspaceRoot.FullName);
                context.AddApplicator(CreateAspireApplicator(workspaceRoot));
            }
            else
            {
                _logger.LogDebug("Aspire MCP server is already configured");
            }

            // Register Playwright configuration callback if not already configured
            if (!HasPlaywrightServerConfigured(workspaceRoot))
            {
                _logger.LogDebug("Registering Playwright MCP configuration callback for Claude Code");
                CommonAgentApplicators.AddPlaywrightConfigurationCallback(
                    context,
                    ct => ApplyPlaywrightMcpConfigurationAsync(workspaceRoot, ct));
            }
            else
            {
                _logger.LogDebug("Playwright MCP server is already configured");
            }

            // Try to add agent instructions applicator (only once across all scanners)
            CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, context.RepositoryRoot);
        }
        else
        {
            // No .claude folder found - check if Claude Code CLI is installed
            _logger.LogDebug("No .claude folder found, checking for Claude Code CLI installation...");
            var claudeCodeVersion = await _claudeCodeCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);

            if (claudeCodeVersion is not null)
            {
                _logger.LogDebug("Found Claude Code CLI version: {Version}", claudeCodeVersion);
                
                // Claude Code is installed - offer to create config at workspace root
                if (!HasAspireServerConfigured(context.RepositoryRoot))
                {
                    _logger.LogDebug("Adding Claude Code applicator for .mcp.json at workspace root: {WorkspaceRoot}", context.RepositoryRoot.FullName);
                    context.AddApplicator(CreateAspireApplicator(context.RepositoryRoot));
                }
                else
                {
                    _logger.LogDebug("Aspire MCP server is already configured");
                }

                // Register Playwright configuration callback if not already configured
                if (!HasPlaywrightServerConfigured(context.RepositoryRoot))
                {
                    _logger.LogDebug("Registering Playwright MCP configuration callback for Claude Code");
                    CommonAgentApplicators.AddPlaywrightConfigurationCallback(
                        context,
                        ct => ApplyPlaywrightMcpConfigurationAsync(context.RepositoryRoot, ct));
                }
                else
                {
                    _logger.LogDebug("Playwright MCP server is already configured");
                }

                // Try to add agent instructions applicator (only once across all scanners)
                CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, context.RepositoryRoot);
            }
            else
            {
                _logger.LogDebug("Claude Code CLI not found - skipping");
            }
        }
    }

    /// <summary>
    /// Walks up the directory tree to find a .claude folder.
    /// Stops if we go above the workspace root.
    /// Ignores the .claude folder in the user's home directory.
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from.</param>
    /// <param name="repositoryRoot">The workspace root to use as the boundary for searches.</param>
    private DirectoryInfo? FindClaudeCodeFolder(DirectoryInfo startDirectory, DirectoryInfo repositoryRoot)
    {
        var currentDirectory = startDirectory;
        var homeDirectory = _executionContext.HomeDirectory;

        while (currentDirectory is not null)
        {
            // Check for .claude folder at current level, but ignore it if it's in the home directory
            // (the home directory's .claude folder is for user settings, not project config)
            var claudeCodePath = Path.Combine(currentDirectory.FullName, ClaudeCodeFolderName);
            if (Directory.Exists(claudeCodePath) && !string.Equals(currentDirectory.FullName, homeDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return new DirectoryInfo(claudeCodePath);
            }

            // Stop if we've reached the workspace root without finding .claude
            // (don't search above the workspace boundary)
            if (string.Equals(currentDirectory.FullName, repositoryRoot.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Checks if the repo root contains an .mcp.json file with an "aspire" MCP server configured.
    /// </summary>
    /// <param name="repoRoot">The repository root directory to check.</param>
    /// <returns>True if the aspire server is already configured, false otherwise.</returns>
    private static bool HasAspireServerConfigured(DirectoryInfo repoRoot)
    {
        var configFilePath = Path.Combine(repoRoot.FullName, McpConfigFileName);

        if (!File.Exists(configFilePath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(configFilePath);
            var config = JsonNode.Parse(content)?.AsObject();

            if (config is null)
            {
                return false;
            }

            if (config.TryGetPropertyValue("mcpServers", out var serversNode) && serversNode is JsonObject servers)
            {
                return servers.ContainsKey(AspireServerName);
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
    /// Checks if the Playwright MCP server is already configured in the .mcp.json file.
    /// </summary>
    private static bool HasPlaywrightServerConfigured(DirectoryInfo repoRoot)
    {
        var configFilePath = Path.Combine(repoRoot.FullName, McpConfigFileName);
        
        if (!File.Exists(configFilePath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(configFilePath);
            var config = JsonNode.Parse(content)?.AsObject();
            if (config is null)
            {
                return false;
            }

            if (config.TryGetPropertyValue("mcpServers", out var serversNode) && serversNode is JsonObject servers)
            {
                return servers.ContainsKey("playwright");
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates an applicator for configuring the Aspire MCP server in the .mcp.json file at the repo root.
    /// </summary>
    private static AgentEnvironmentApplicator CreateAspireApplicator(DirectoryInfo repoRoot)
    {
        return new AgentEnvironmentApplicator(
            ClaudeCodeAgentEnvironmentScannerStrings.ApplicatorDescription,
            async cancellationToken => await ApplyAspireMcpConfigurationAsync(repoRoot, cancellationToken));
    }

    /// <summary>
    /// Creates or updates the .mcp.json file at the repo root with Aspire MCP configuration.
    /// </summary>
    private static async Task ApplyAspireMcpConfigurationAsync(
        DirectoryInfo repoRoot,
        CancellationToken cancellationToken)
    {
        var configFilePath = Path.Combine(repoRoot.FullName, McpConfigFileName);
        JsonObject config;

        // Read existing config or create new
        if (File.Exists(configFilePath))
        {
            var existingContent = await File.ReadAllTextAsync(configFilePath, cancellationToken);
            config = JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();
        }
        else
        {
            config = new JsonObject();
        }

        // Ensure "mcpServers" object exists
        if (!config.ContainsKey("mcpServers") || config["mcpServers"] is not JsonObject)
        {
            config["mcpServers"] = new JsonObject();
        }

        var servers = config["mcpServers"]!.AsObject();

        // Add or update the "aspire" server configuration
        servers[AspireServerName] = new JsonObject
        {
            ["command"] = "aspire",
            ["args"] = new JsonArray("mcp", "start")
        };

        // Write the updated config using AOT-compatible serialization
        var jsonContent = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);
        await File.WriteAllTextAsync(configFilePath, jsonContent, cancellationToken);
    }

    /// <summary>
    /// Creates or updates the .mcp.json file at the repo root with Playwright MCP configuration.
    /// </summary>
    private static async Task ApplyPlaywrightMcpConfigurationAsync(
        DirectoryInfo repoRoot,
        CancellationToken cancellationToken)
    {
        var configFilePath = Path.Combine(repoRoot.FullName, McpConfigFileName);
        JsonObject config;

        // Read existing config or create new
        if (File.Exists(configFilePath))
        {
            var existingContent = await File.ReadAllTextAsync(configFilePath, cancellationToken);
            config = JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();
        }
        else
        {
            config = new JsonObject();
        }

        // Ensure "mcpServers" object exists
        if (!config.ContainsKey("mcpServers") || config["mcpServers"] is not JsonObject)
        {
            config["mcpServers"] = new JsonObject();
        }

        var servers = config["mcpServers"]!.AsObject();

        // Add Playwright MCP server configuration
        servers["playwright"] = new JsonObject
        {
            ["command"] = "npx",
            ["args"] = new JsonArray("-y", "@playwright/mcp@latest")
        };

        // Write the updated config using AOT-compatible serialization
        var jsonContent = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);
        await File.WriteAllTextAsync(configFilePath, jsonContent, cancellationToken);
    }
}
