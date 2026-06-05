// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.VsCode;

/// <summary>
/// Scans for VS Code environments and provides an applicator to configure the Aspire MCP server.
/// </summary>
internal sealed class VsCodeAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private const string VsCodeFolderName = ".vscode";
    private const string McpConfigFileName = "mcp.json";
    private const string AspireServerName = "aspire";

    private readonly IVsCodeCliRunner _vsCodeCliRunner;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<VsCodeAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="VsCodeAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="vsCodeCliRunner">The VS Code CLI runner for checking if VS Code is installed.</param>
    /// <param name="executionContext">The CLI execution context for accessing environment variables and settings.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public VsCodeAgentEnvironmentScanner(IVsCodeCliRunner vsCodeCliRunner, CliExecutionContext executionContext, ILogger<VsCodeAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(vsCodeCliRunner);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);
        _vsCodeCliRunner = vsCodeCliRunner;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting VS Code environment scan in directory: {WorkingDirectory}", context.WorkingDirectory.FullName);
        _logger.LogDebug("Workspace root: {RepositoryRoot}", context.RepositoryRoot.FullName);
        
        _logger.LogDebug("Searching for .vscode folder...");
        var vsCodeFolder = FindVsCodeFolder(context.WorkingDirectory, context.RepositoryRoot);

        if (vsCodeFolder is not null)
        {
            _logger.LogDebug("Found .vscode folder at: {VsCodeFolder}", vsCodeFolder.FullName);
            
            // Check if the aspire server is already configured
            if (!HasAspireServerConfigured(vsCodeFolder))
            {
                // Found a .vscode folder - add an applicator to configure MCP
                _logger.LogDebug("Adding VS Code applicator for .vscode folder at: {VsCodeFolder}", vsCodeFolder.FullName);
                context.AddApplicator(CreateAspireApplicator(vsCodeFolder));
            }
            else
            {
                _logger.LogDebug("Aspire MCP server is already configured in .vscode/mcp.json");
            }

            // Register Playwright configuration callback if not already configured
            if (!HasPlaywrightServerConfigured(vsCodeFolder))
            {
                _logger.LogDebug("Registering Playwright MCP configuration callback for .vscode folder");
                CommonAgentApplicators.AddPlaywrightConfigurationCallback(
                    context,
                    ct => ApplyPlaywrightMcpConfigurationAsync(vsCodeFolder, ct));
            }
            else
            {
                _logger.LogDebug("Playwright MCP server is already configured in .vscode/mcp.json");
            }

            // Try to add agent instructions applicator (only once across all scanners)
            CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, context.RepositoryRoot);
        }
        else if (await IsVsCodeAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("No .vscode folder found, but VS Code is available on the system");
            // No .vscode folder found, but VS Code is available
            // Use workspace root for new .vscode folder
            var targetVsCodeFolder = new DirectoryInfo(Path.Combine(context.RepositoryRoot.FullName, VsCodeFolderName));
            _logger.LogDebug("Adding VS Code applicator for new .vscode folder at: {VsCodeFolder}", targetVsCodeFolder.FullName);
            context.AddApplicator(CreateAspireApplicator(targetVsCodeFolder));
            
            // Register Playwright configuration callback
            CommonAgentApplicators.AddPlaywrightConfigurationCallback(
                context,
                ct => ApplyPlaywrightMcpConfigurationAsync(targetVsCodeFolder, ct));
            
            // Try to add agent instructions applicator (only once across all scanners)
            CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, context.RepositoryRoot);
        }
        else
        {
            _logger.LogDebug("No .vscode folder found and VS Code is not available - skipping VS Code configuration");
        }
    }

    /// <summary>
    /// Checks if VS Code is available on the machine.
    /// First checks for VS Code environment variables (low cost),
    /// then falls back to checking for the CLI executables.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if VS Code is available, false otherwise.</returns>
    private async Task<bool> IsVsCodeAvailableAsync(CancellationToken cancellationToken)
    {
        // First check environment variables (low cost)
        _logger.LogDebug("Checking for VS Code environment variables...");
        if (HasVsCodeEnvironmentVariables())
        {
            _logger.LogDebug("Found VS Code environment variables");
            return true;
        }

        // Try VS Code stable
        _logger.LogDebug("Checking for VS Code stable CLI...");
        var vsCodeVersion = await _vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = false }, cancellationToken).ConfigureAwait(false);
        if (vsCodeVersion is not null)
        {
            _logger.LogDebug("Found VS Code stable version: {Version}", vsCodeVersion);
            return true;
        }

        // Try VS Code Insiders
        _logger.LogDebug("Checking for VS Code Insiders CLI...");
        var vsCodeInsidersVersion = await _vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = true }, cancellationToken).ConfigureAwait(false);
        if (vsCodeInsidersVersion is not null)
        {
            _logger.LogDebug("Found VS Code Insiders version: {Version}", vsCodeInsidersVersion);
            return true;
        }

        _logger.LogDebug("VS Code not found on the system");
        return false;
    }

    /// <summary>
    /// Walks up the directory tree to find a .vscode folder.
    /// Stops if we go above the workspace root.
    /// Ignores the .vscode folder in the user's home directory (used for user settings, not workspace config).
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from.</param>
    /// <param name="repositoryRoot">The workspace root to use as the boundary for searches.</param>
    private DirectoryInfo? FindVsCodeFolder(DirectoryInfo startDirectory, DirectoryInfo repositoryRoot)
    {
        var currentDirectory = startDirectory;
        var homeDirectory = _executionContext.HomeDirectory;

        while (currentDirectory is not null)
        {
            // Check for .vscode folder at current level, but ignore it if it's in the home directory
            // (the home directory's .vscode folder is for user settings, not workspace config)
            var vsCodePath = Path.Combine(currentDirectory.FullName, VsCodeFolderName);
            if (Directory.Exists(vsCodePath) && !string.Equals(currentDirectory.FullName, homeDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return new DirectoryInfo(vsCodePath);
            }

            // Stop if we've reached the workspace root without finding .vscode
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
    /// Checks if any VS Code environment variables are present.
    /// </summary>
    private bool HasVsCodeEnvironmentVariables()
    {
        if (_executionContext.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if the .vscode folder contains an mcp.json file with an "aspire" server configured.
    /// </summary>
    /// <param name="vsCodeFolder">The .vscode folder to check.</param>
    /// <returns>True if the aspire server is already configured, false otherwise.</returns>
    private static bool HasAspireServerConfigured(DirectoryInfo vsCodeFolder)
    {
        var mcpConfigPath = Path.Combine(vsCodeFolder.FullName, McpConfigFileName);

        if (!File.Exists(mcpConfigPath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(mcpConfigPath);
            var config = JsonNode.Parse(content)?.AsObject();

            if (config is null)
            {
                return false;
            }

            if (config.TryGetPropertyValue("servers", out var serversNode) && serversNode is JsonObject servers)
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
    /// Checks if the Playwright MCP server is already configured in the mcp.json file.
    /// </summary>
    private static bool HasPlaywrightServerConfigured(DirectoryInfo vsCodeFolder)
    {
        var mcpConfigPath = Path.Combine(vsCodeFolder.FullName, McpConfigFileName);
        
        if (!File.Exists(mcpConfigPath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(mcpConfigPath);
            var config = JsonNode.Parse(content)?.AsObject();
            if (config is null)
            {
                return false;
            }

            if (config.TryGetPropertyValue("servers", out var serversNode) && serversNode is JsonObject servers)
            {
                return servers.ContainsKey("playwright");
            }

            return false;
        }
        catch (JsonException)
        {
            // If the JSON is malformed, assume playwright is not configured
            return false;
        }
    }

    /// <summary>
    /// Creates an applicator for configuring the Aspire MCP server in the specified .vscode folder.
    /// </summary>
    private static AgentEnvironmentApplicator CreateAspireApplicator(DirectoryInfo vsCodeFolder)
    {
        return new AgentEnvironmentApplicator(
            VsCodeAgentEnvironmentScannerStrings.ApplicatorDescription,
            async cancellationToken => await ApplyAspireMcpConfigurationAsync(vsCodeFolder, cancellationToken));
    }

    /// <summary>
    /// Creates or updates the mcp.json file in the .vscode folder with Aspire MCP configuration.
    /// </summary>
    private static async Task ApplyAspireMcpConfigurationAsync(
        DirectoryInfo vsCodeFolder,
        CancellationToken cancellationToken)
    {
        // Ensure the .vscode folder exists
        if (!vsCodeFolder.Exists)
        {
            vsCodeFolder.Create();
        }

        var mcpConfigPath = Path.Combine(vsCodeFolder.FullName, McpConfigFileName);
        JsonObject config;

        // Read existing config or create new
        if (File.Exists(mcpConfigPath))
        {
            var existingContent = await File.ReadAllTextAsync(mcpConfigPath, cancellationToken);
            config = JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();
        }
        else
        {
            config = new JsonObject();
        }

        // Ensure "servers" object exists
        if (!config.ContainsKey("servers") || config["servers"] is not JsonObject)
        {
            config["servers"] = new JsonObject();
        }

        var servers = config["servers"]!.AsObject();

        // Add or update the "aspire" server configuration
        servers[AspireServerName] = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = "aspire",
            ["args"] = new JsonArray("mcp", "start")
        };

        // Write the updated config with indentation using AOT-compatible serialization
        var jsonContent = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);
        await File.WriteAllTextAsync(mcpConfigPath, jsonContent, cancellationToken);
    }

    /// <summary>
    /// Creates or updates the mcp.json file in the .vscode folder with Playwright MCP configuration.
    /// </summary>
    private static async Task ApplyPlaywrightMcpConfigurationAsync(
        DirectoryInfo vsCodeFolder,
        CancellationToken cancellationToken)
    {
        // Ensure the .vscode folder exists
        if (!vsCodeFolder.Exists)
        {
            vsCodeFolder.Create();
        }

        var mcpConfigPath = Path.Combine(vsCodeFolder.FullName, McpConfigFileName);
        JsonObject config;

        // Read existing config or create new
        if (File.Exists(mcpConfigPath))
        {
            var existingContent = await File.ReadAllTextAsync(mcpConfigPath, cancellationToken);
            config = JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();
        }
        else
        {
            config = new JsonObject();
        }

        // Ensure "servers" object exists
        if (!config.ContainsKey("servers") || config["servers"] is not JsonObject)
        {
            config["servers"] = new JsonObject();
        }

        var servers = config["servers"]!.AsObject();

        // Add Playwright MCP server configuration
        servers["playwright"] = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = "npx",
            ["args"] = new JsonArray("-y", "@playwright/mcp@latest")
        };

        // Write the updated config with indentation using AOT-compatible serialization
        var jsonContent = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);
        await File.WriteAllTextAsync(mcpConfigPath, jsonContent, cancellationToken);
    }
}
