// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Scans for GitHub Copilot App and CLI environments and provides their shared configuration applicators.
/// </summary>
internal sealed class CopilotAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private const string McpConfigFileName = "mcp-config.json";
    private const string AspireServerName = "aspire";
    private static readonly string s_skillBaseDirectory = Path.Combine(".github", "skills");

    private readonly ICopilotCliRunner _copilotCliRunner;
    private readonly ICopilotAppInstallationDetector _copilotAppInstallationDetector;
    private readonly PlaywrightCliInstaller _playwrightCliInstaller;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<CopilotAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CopilotAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="copilotCliRunner">The Copilot CLI runner for checking if Copilot CLI is installed.</param>
    /// <param name="copilotAppInstallationDetector">The detector for checking if the Copilot App is installed.</param>
    /// <param name="playwrightCliInstaller">The Playwright CLI installer for secure installation.</param>
    /// <param name="executionContext">The CLI execution context for accessing environment variables and settings.</param>
    /// <param name="environment">The environment abstraction for reading environment variables.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public CopilotAgentEnvironmentScanner(
        ICopilotCliRunner copilotCliRunner,
        ICopilotAppInstallationDetector copilotAppInstallationDetector,
        PlaywrightCliInstaller playwrightCliInstaller,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<CopilotAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(copilotCliRunner);
        ArgumentNullException.ThrowIfNull(copilotAppInstallationDetector);
        ArgumentNullException.ThrowIfNull(playwrightCliInstaller);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _copilotCliRunner = copilotCliRunner;
        _copilotAppInstallationDetector = copilotAppInstallationDetector;
        _playwrightCliInstaller = playwrightCliInstaller;
        _executionContext = executionContext;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Starting GitHub Copilot environment scan");

        var homeDirectory = _executionContext.HomeDirectory;
        var copilotAppDetected = false;
        if (_copilotAppInstallationDetector.GetInstallationMarker() is { } installationMarker)
        {
            _logger.LogDebug("Detected GitHub Copilot App using installation marker {Marker}", installationMarker);
            context.AddDetectedClient(AgentClientKind.CopilotApp);
            copilotAppDetected = true;
        }

        var copilotCliDetected = false;

        // Check if we're running in a VSCode terminal.
        var isVSCode = _environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode";
        if (isVSCode)
        {
            _logger.LogDebug("Detected VSCode terminal environment. Assuming GitHub Copilot CLI is available to avoid potential hangs from interactive installation prompts.");
            context.AddDetectedClient(AgentClientKind.CopilotCli);
            copilotCliDetected = true;
        }
        else
        {
            _logger.LogDebug("Checking for GitHub Copilot CLI installation...");
            var copilotVersion = await _copilotCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (copilotVersion is not null)
            {
                _logger.LogDebug("Found GitHub Copilot CLI version: {Version}", copilotVersion);
                context.AddDetectedClient(AgentClientKind.CopilotCli);
                copilotCliDetected = true;
            }
            else
            {
                _logger.LogDebug("GitHub Copilot CLI is not installed");
            }
        }

        if (!copilotAppDetected && !copilotCliDetected)
        {
            _logger.LogDebug("No GitHub Copilot environments are installed - skipping");
            return;
        }

        // The Copilot App automatically loads MCP servers and skills configured for Copilot CLI.
        // See https://docs.github.com/en/copilot/how-tos/github-copilot-app/customize-github-copilot-app.
        // Configure the shared Copilot locations once when either client is present.
        var configDirectory = CopilotPaths.GetConfigDirectory(homeDirectory, _environment);
        _logger.LogDebug("Checking if Aspire MCP server is already configured in GitHub Copilot");
        if (!HasAspireServerConfigured(configDirectory))
        {
            _logger.LogDebug("Adding GitHub Copilot applicator for global MCP configuration");
            context.AddApplicator(CreateApplicator(configDirectory));
        }
        else
        {
            _logger.LogDebug("Aspire MCP server is already configured in GitHub Copilot");
        }

        CommonAgentApplicators.AddPlaywrightCliApplicator(context, _playwrightCliInstaller, s_skillBaseDirectory);
    }

    /// <summary>
    /// Gets the path to the GitHub Copilot MCP configuration file.
    /// </summary>
    /// <param name="configDirectory">The GitHub Copilot configuration directory.</param>
    private static string GetMcpConfigFilePath(string configDirectory)
    {
        return Path.Combine(configDirectory, McpConfigFileName);
    }

    /// <summary>
    /// Checks if the GitHub Copilot global configuration has an "aspire" MCP server configured.
    /// </summary>
    /// <param name="configDirectory">The GitHub Copilot configuration directory.</param>
    /// <returns>True if the aspire server is already configured, false otherwise.</returns>
    private static bool HasAspireServerConfigured(string configDirectory)
        => McpConfigFileHelper.HasServerConfigured(
            GetMcpConfigFilePath(configDirectory),
            "mcpServers",
            AspireServerName);

    /// <summary>
    /// Creates an applicator for configuring the MCP server in the GitHub Copilot global configuration.
    /// </summary>
    /// <param name="configDirectory">The GitHub Copilot configuration directory.</param>
    private static AgentEnvironmentApplicator CreateApplicator(string configDirectory)
    {
        return new AgentEnvironmentApplicator(
            CopilotAgentEnvironmentScannerStrings.ApplicatorDescription,
            ct => ApplyMcpConfigurationAsync(
                configDirectory,
                ct));
    }

    /// <summary>
    /// Creates or updates the mcp-config.json file in the GitHub Copilot global configuration directory.
    /// </summary>
    /// <param name="configDirectory">The GitHub Copilot configuration directory.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    private static async Task ApplyMcpConfigurationAsync(
        string configDirectory,
        CancellationToken cancellationToken)
    {
        var configFilePath = GetMcpConfigFilePath(configDirectory);

        // Ensure the .copilot directory exists
        if (!Directory.Exists(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }

        var config = await McpConfigFileHelper.ReadConfigAsync(configFilePath, null, cancellationToken);

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
            ["args"] = new JsonArray("agent", "mcp"),
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

}
