// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Agents.ClaudeCode;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Discovers Copilot App or CLI and configures their shared environment.
/// </summary>
internal sealed class CopilotAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    private readonly ICopilotCliRunner _copilotCliRunner;
    private readonly ICopilotAppInstallationDetector _copilotAppInstallationDetector;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<CopilotAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CopilotAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="copilotCliRunner">The Copilot CLI runner for checking if Copilot CLI is installed.</param>
    /// <param name="copilotAppInstallationDetector">The detector for checking if the Copilot App is installed.</param>
    /// <param name="executionContext">The CLI execution context for resolving workspace and user configuration paths.</param>
    /// <param name="environment">The environment abstraction for reading environment variables.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public CopilotAgentEnvironmentScanner(
        ICopilotCliRunner copilotCliRunner,
        ICopilotAppInstallationDetector copilotAppInstallationDetector,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<CopilotAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(copilotCliRunner);
        ArgumentNullException.ThrowIfNull(copilotAppInstallationDetector);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _copilotCliRunner = copilotCliRunner;
        _copilotAppInstallationDetector = copilotAppInstallationDetector;
        _executionContext = executionContext;
        _environment = environment;
        _logger = logger;
    }

    internal const string ClientId = "copilot";
    internal const string HookEventName = "postToolUse";

    /// <inheritdoc />
    public async Task<AgentEnvironmentDetection?> ScanAsync(DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Starting GitHub Copilot environment scan");

        var appInstalled = false;
        string? cliVersion = null;
        if (_copilotAppInstallationDetector.GetInstallationMarker() is { } installationMarker)
        {
            _logger.LogDebug("Detected GitHub Copilot App using installation marker {Marker}", installationMarker);
            appInstalled = true;
        }

        // VS Code can supply an interactive Copilot installation shim. Do not invoke it during
        // discovery, where an installation prompt could hang the enclosing command.
        if (_environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            _logger.LogDebug("Detected VS Code terminal environment. Skipping the Copilot CLI version probe.");
            return appInstalled ? new(Version: null, IsInsiders: false) : null;
        }
        else
        {
            var version = await _copilotCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                _logger.LogDebug("Found GitHub Copilot CLI version: {Version}", version);
                cliVersion = version.ToString();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return appInstalled || cliVersion is not null ? new(cliVersion, IsInsiders: false) : null;
    }

    /// <inheritdoc />
    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        foreach (var target in GetPluginTargets(request))
        {
            yield return target;
        }

        var copilotDirectory = CopilotPaths.GetConfigDirectory(_executionContext, _environment);
        var mcpClients = request.Clients.Where(client => client.Environment == this).ToArray();
        if (request.Assets.Mcp && mcpClients.Length > 0)
        {
            var rootMcp = Path.Combine(request.WorkspaceRoot.FullName, ".mcp.json");
            // Copilot accepts stdio and the root .mcp.json schema used by Claude. Share that
            // physical entry when applicable; do not create an ineffective second overlay.
            // https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers
            var projectPath = File.Exists(rootMcp) || request.Clients.Any(client => client.Environment is ClaudeCodeAgentEnvironmentScanner)
                ? rootMcp
                : Path.Combine(request.WorkspaceRoot.FullName, ".github", "mcp.json");
            yield return McpTarget(projectPath, AgentConfigurationScope.Project, copilotEnvironment: false);
            yield return McpTarget(Path.Combine(copilotDirectory, "mcp-config.json"), AgentConfigurationScope.User, copilotEnvironment: true);
        }

        AgentConfigurationTarget McpTarget(string path, AgentConfigurationScope scope, bool copilotEnvironment)
            => new(path, scope, AgentAssetKind.Mcp, mcpClients, "mcpServers:aspire", async (root, context, cancellationToken) =>
            {
                var settings = await AgentConfigurationJson.ReadSettingsAsync(context, CopilotPaths.PluginSettings(request, _executionContext, _environment), cancellationToken);
                var managed = await AgentConfigurationJson.ReadSettingsAsync(context, CopilotPaths.ManagedSettings(_executionContext, _environment), cancellationToken);
                if (AspireMcpConfiguration.CheckPolicy(settings, managed, managedAllowlistOnly: true) is { } policy)
                {
                    return policy;
                }

                foreach (var other in await AgentConfigurationJson.ReadSettingsAsync(context,
                    [
                        Path.Combine(copilotDirectory, "mcp-config.json"),
                        Path.Combine(request.WorkspaceRoot.FullName, ".github", "mcp.json"),
                        Path.Combine(request.WorkspaceRoot.FullName, ".mcp.json")
                    ], cancellationToken))
                {
                    if (AspireMcpConfiguration.CheckPolicy([other]) is { } disabled)
                    {
                        return disabled;
                    }

                    var servers = AspireMcpConfiguration.UsesBareServers(other) ? other : AgentConfigurationJson.OptionalObject(other, "mcpServers");
                    if (AspireMcpConfiguration.CheckExistingEntry(servers, commandArray: false,
                        () => AspireMcpConfiguration.UsesBareServers(root) ? root : AgentConfigurationJson.OptionalObject(root, "mcpServers")) is { } existing)
                    {
                        return existing;
                    }
                }

                var addCopilotDefaults = copilotEnvironment &&
                    AgentConfigurationJson.OptionalObject(root, "mcpServers")?.ContainsKey(AspireMcpConfiguration.ServerName) is not true;
                var edit = AspireMcpConfiguration.Apply(root, "mcpServers", commandArray: false, "stdio",
                    bare: scope is AgentConfigurationScope.Project && AspireMcpConfiguration.UsesBareServers(root));
                if (addCopilotDefaults && edit.Status is AgentConfigurationStatus.Configured)
                {
                    // Copilot does not inherit arbitrary _environment variables for local MCP
                    // servers. Preserve Aspire's existing DOTNET_ROOT pass-through contract.
                    var server = root["mcpServers"]![AspireMcpConfiguration.ServerName]!.AsObject();
                    server["env"] = new JsonObject { ["DOTNET_ROOT"] = "${DOTNET_ROOT}" };
                    server["tools"] = new JsonArray("*");
                }

                return edit;
            });
    }

    private IEnumerable<AgentConfigurationTarget> GetPluginTargets(AgentInitRequest request)
    {
        var clients = request.Clients.Where(client => client.Environment == this).ToArray();
        if (request.Assets.AspireSkills && clients.Length > 0)
        {
            yield return Target(Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json"), AgentConfigurationScope.Project);
            yield return Target(Path.Combine(CopilotPaths.GetConfigDirectory(_executionContext, _environment), "settings.json"), AgentConfigurationScope.User);
        }

        AgentConfigurationTarget Target(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.AspireSkills, clients, "plugins:aspire", async (root, context, cancellationToken) =>
                AspireSkillsPluginConfiguration.Apply(root, await AgentConfigurationJson.ReadSettingsAsync(
                    context, CopilotPaths.PluginSettings(request, _executionContext, _environment), cancellationToken)));
    }

    public static void ValidateHooks(JsonObject root)
    {
        if (root.TryGetPropertyValue("version", out var version) &&
            (version is not JsonValue number || !number.TryGetValue<int>(out var value) || value != 1))
        {
            throw AgentConfigurationJson.Shape("version");
        }

        if (AgentConfigurationJson.OptionalObject(root, "hooks") is { } hooks &&
            hooks.TryGetPropertyValue(HookEventName, out var entries) &&
            (entries is not JsonArray array || array.Any(entry => entry is not JsonObject)))
        {
            throw AgentConfigurationJson.Shape($"hooks.{HookEventName}");
        }
    }

    public static void ApplyHook(JsonObject root, TelemetryHookScripts scripts, Func<JsonNode?, bool> isAspireHook)
    {
        root["version"] = 1;
        var hooks = AgentConfigurationJson.Object(root, "hooks");
        var entries = hooks[HookEventName] as JsonArray ?? new JsonArray();
        if (!hooks.ContainsKey(HookEventName))
        {
            hooks[HookEventName] = entries;
        }

        var desired = new JsonObject
        {
            ["type"] = "command",
            ["bash"] = HookCommandFormatter.BuildBashCommand(scripts.ShellScriptPath),
            ["powershell"] = HookCommandFormatter.BuildPwshCommand(scripts.PowerShellScriptPath),
            ["timeoutSec"] = TelemetryHookConfigurator.HookTimeoutSeconds
        };
        var owned = entries.Where(isAspireHook).ToArray();
        if (owned.Length == 1 && JsonNode.DeepEquals(owned[0], desired))
        {
            return;
        }

        foreach (var entry in owned)
        {
            entries.Remove(entry);
        }

        entries.Add((JsonNode)desired);
    }
}
