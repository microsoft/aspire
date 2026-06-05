// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.CopilotCli;

/// <summary>
/// Scans for GitHub Copilot CLI environments and provides an applicator to configure the Aspire MCP server.
/// </summary>
internal sealed class CopilotCliAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private const string CopilotFolderName = ".copilot";
    private const string McpConfigFileName = "mcp-config.json";
    private const string AspireServerName = "aspire";

    private readonly ICopilotCliRunner _copilotCliRunner;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<CopilotCliAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CopilotCliAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="copilotCliRunner">The Copilot CLI runner for checking if Copilot CLI is installed.</param>
    /// <param name="executionContext">The CLI execution context for accessing environment variables and settings.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public CopilotCliAgentEnvironmentScanner(ICopilotCliRunner copilotCliRunner, CliExecutionContext executionContext, ILogger<CopilotCliAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(copilotCliRunner);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);
        _copilotCliRunner = copilotCliRunner;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting GitHub Copilot CLI environment scan");

        var homeDirectory = _executionContext.HomeDirectory;
        
        // Check if we're running in a VSCode terminal
        var isVSCode = _executionContext.GetEnvironmentVariable("TERM_PROGRAM") == "vscode";
        
        if (isVSCode)
        {
            _logger.LogDebug("Detected VSCode terminal environment. Assuming GitHub Copilot CLI is available to avoid potential hangs from interactive installation prompts.");
            
            // Check if the aspire server is already configured in the global config
            _logger.LogDebug("Checking if Aspire MCP server is already configured in Copilot CLI global config...");
            if (!HasAspireServerConfigured(homeDirectory))
            {
                // In VSCode, assume Copilot CLI is available and offer to configure
                // The user will be prompted to install it when they try to use it if not already installed
                _logger.LogDebug("Adding Copilot CLI applicator for global MCP configuration");
                context.AddApplicator(CreateApplicator(homeDirectory));
            }
            else
            {
                _logger.LogDebug("Aspire MCP server is already configured in Copilot CLI");
            }

            // Register Playwright configuration callback if not already configured
            if (!HasPlaywrightServerConfigured(homeDirectory))
            {
                _logger.LogDebug("Registering Playwright MCP configuration callback for Copilot CLI");
                CommonAgentApplicators.AddPlaywrightConfigurationCallback(
                    context,
                    ct => ApplyPlaywrightMcpConfigurationAsync(homeDirectory, ct));
            }
            else
            {
                _logger.LogDebug("Playwright MCP server is already configured in Copilot CLI");
            }

            // Try to add agent instructions applicator (only once across all scanners)
            CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, context.RepositoryRoot);
            return;
        }
        
        // Check if Copilot CLI is installed
        _logger.LogDebug("Checking for GitHub Copilot CLI installation...");
        var copilotVersion = await _copilotCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);

        if (copilotVersion is null)
        {
            _logger.LogDebug("GitHub Copilot CLI is not installed - skipping");
            // Copilot CLI is not installed, no need to offer configuration
            return;
        }

        _logger.LogDebug("Found GitHub Copilot CLI version: {Version}", copilotVersion);

        // Check if the aspire server is already configured in the global config
        _logger.LogDebug("Checking if Aspire MCP server is already configured in Copilot CLI global config...");
        if (!HasAspireServerConfigured(homeDirectory))
        {
            // Copilot CLI is installed and aspire is not configured - offer to configure
            _logger.LogDebug("Adding Copilot CLI applicator for global MCP configuration");
            context.AddApplicator(CreateApplicator(homeDirectory));
        }
        else
        {
            _logger.LogDebug("Aspire MCP server is already configured in Copilot CLI");
        }

        // Register Playwright configuration callback if not already configured
        if (!HasPlaywrightServerConfigured(homeDirectory))
        {
            _logger.LogDebug("Registering Playwright MCP configuration callback for Copilot CLI");
            CommonAgentApplicators.AddPlaywrightConfigurationCallback(
                context,
                ct => ApplyPlaywrightMcpConfigurationAsync(homeDirectory, ct));
        }
        else
        {
            _logger.LogDebug("Playwright MCP server is already configured in Copilot CLI");
        }

        // Try to add agent instructions applicator (only once across all scanners)
        CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, context.RepositoryRoot);
    }

    /// <summary>
    /// Gets the path to the Copilot CLI global configuration directory.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    private static string GetCopilotConfigDirectory(DirectoryInfo homeDirectory)
    {
        return Path.Combine(homeDirectory.FullName, CopilotFolderName);
    }

    /// <summary>
    /// Gets the path to the Copilot CLI MCP configuration file.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    private static string GetMcpConfigFilePath(DirectoryInfo homeDirectory)
    {
        return Path.Combine(GetCopilotConfigDirectory(homeDirectory), McpConfigFileName);
    }

    /// <summary>
    /// Checks if the Copilot CLI global configuration has an "aspire" MCP server configured.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>True if the aspire server is already configured, false otherwise.</returns>
    private static bool HasAspireServerConfigured(DirectoryInfo homeDirectory)
    {
        var configFilePath = GetMcpConfigFilePath(homeDirectory);

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
    /// Creates an applicator for configuring the MCP server in the Copilot CLI global configuration.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    private static AgentEnvironmentApplicator CreateApplicator(DirectoryInfo homeDirectory)
    {
        return new AgentEnvironmentApplicator(
            CopilotCliAgentEnvironmentScannerStrings.ApplicatorDescription,
            ct => ApplyMcpConfigurationAsync(
                homeDirectory,
                ct));
    }

    /// <summary>
    /// Creates or updates the mcp-config.json file in the Copilot CLI global configuration directory.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    private static async Task ApplyMcpConfigurationAsync(
        DirectoryInfo homeDirectory,
        CancellationToken cancellationToken)
    {
        var configDirectory = GetCopilotConfigDirectory(homeDirectory);
        var configFilePath = GetMcpConfigFilePath(homeDirectory);

        // Ensure the .copilot directory exists
        if (!Directory.Exists(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }

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

        // Add or update the "aspire" server configuration with DOTNET_ROOT environment variable passthrough
        servers[AspireServerName] = new JsonObject
        {
            ["type"] = "local",
            ["command"] = "aspire",
            ["args"] = new JsonArray("mcp", "start"),
            ["env"] = new JsonObject
            {
                ["DOTNET_ROOT"] = "${DOTNET_ROOT}"
            },
            ["tools"] = new JsonArray("*")
        };

        // Write the updated config using AOT-compatible serialization
        var jsonContent = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);
        await File.WriteAllTextAsync(configFilePath, jsonContent, cancellationToken);
    }

    /// <summary>
    /// Creates or updates the mcp-config.json file with Playwright MCP configuration.
    /// </summary>
    private static async Task ApplyPlaywrightMcpConfigurationAsync(
        DirectoryInfo homeDirectory,
        CancellationToken cancellationToken)
    {
        var configDirectory = GetCopilotConfigDirectory(homeDirectory);
        var configFilePath = GetMcpConfigFilePath(homeDirectory);

        // Ensure the .copilot directory exists
        if (!Directory.Exists(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }

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
            ["type"] = "local",
            ["command"] = "npx",
            ["args"] = new JsonArray("-y", "@playwright/mcp@latest"),
            ["tools"] = new JsonArray("*")
        };

        // Write the updated config using AOT-compatible serialization
        var jsonContent = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);
        await File.WriteAllTextAsync(configFilePath, jsonContent, cancellationToken);
    }

    /// <summary>
    /// Checks if the Playwright MCP server is already configured in the mcp-config.json file.
    /// </summary>
    private static bool HasPlaywrightServerConfigured(DirectoryInfo homeDirectory)
    {
        var configFilePath = GetMcpConfigFilePath(homeDirectory);
        
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
}
