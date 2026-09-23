// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Discovers Copilot App or CLI and configures their shared environment.
/// </summary>
internal sealed class CopilotAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    internal const string ClientId = "copilot";
    internal const string HookEventName = "postToolUse";

    private readonly ICopilotCliRunner _copilotCliRunner;
    private readonly ICopilotAppInstallationDetector _copilotAppInstallationDetector;
    private readonly IVsCodeCliRunner _vsCodeCliRunner;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<CopilotAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CopilotAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="copilotCliRunner">The Copilot CLI runner for checking if Copilot CLI is installed.</param>
    /// <param name="copilotAppInstallationDetector">The detector for checking if the Copilot App is installed.</param>
    /// <param name="vsCodeCliRunner">The runner used to discover editor-hosted Copilot.</param>
    /// <param name="executionContext">The CLI execution context for resolving workspace and user configuration paths.</param>
    /// <param name="environment">The environment abstraction for reading environment variables.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public CopilotAgentEnvironmentScanner(
        ICopilotCliRunner copilotCliRunner,
        ICopilotAppInstallationDetector copilotAppInstallationDetector,
        IVsCodeCliRunner vsCodeCliRunner,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<CopilotAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(copilotCliRunner);
        ArgumentNullException.ThrowIfNull(copilotAppInstallationDetector);
        ArgumentNullException.ThrowIfNull(vsCodeCliRunner);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _copilotCliRunner = copilotCliRunner;
        _copilotAppInstallationDetector = copilotAppInstallationDetector;
        _vsCodeCliRunner = vsCodeCliRunner;
        _executionContext = executionContext;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => ClientId;

    public string DisplayName => AgentCommandStrings.Environment_Copilot;

    public string Description => AgentCommandStrings.Agent_CopilotPlatforms;

    public override string ToString() => Id;

    /// <inheritdoc />
    public async Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Starting GitHub Copilot environment scan");

        if (_copilotAppInstallationDetector.GetInstallationMarker() is { } installationMarker)
        {
            _logger.LogDebug("Detected GitHub Copilot App using installation marker {Marker}", installationMarker);
            context.AddDetection(new(AgentClientKind.CopilotApp, Version: null, IsInsiders: false));
        }

        // VS Code can supply an interactive Copilot installation shim. Do not invoke it during
        // discovery, where an installation prompt could hang the enclosing command.
        if (_environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            _logger.LogDebug("Detected VS Code terminal environment. Skipping the Copilot CLI version probe.");
        }
        else
        {
            var version = await _copilotCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                _logger.LogDebug("Found GitHub Copilot CLI version: {Version}", version);
                context.AddDetection(new(AgentClientKind.CopilotCli, version.ToString(), IsInsiders: false));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!context.DetectedClients.Any(detection => detection.Client is AgentClientKind.CopilotApp or AgentClientKind.CopilotCli))
        {
            await AddVsCodeHintAsync(context, cancellationToken);
        }
    }

    private async Task AddVsCodeHintAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        var isVsCodeTerminal = _environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode";
        var hasProjectConfiguration = AgentPath.ProjectDirectories(context.WorkingDirectory, context.WorkspaceRoot).Any(directory =>
            Path.GetRelativePath(_executionContext.HomeDirectory.FullName, directory.FullName) != "." &&
            Directory.Exists(Path.Combine(directory.FullName, ".vscode")));
        if (isVsCodeTerminal || hasProjectConfiguration)
        {
            var version = isVsCodeTerminal ? _environment.GetEnvironmentVariable("TERM_PROGRAM_VERSION")?.Trim() : null;
            context.AddDetection(new(AgentClientKind.VsCode, string.IsNullOrEmpty(version) ? null : version,
                IsInsiders: version?.Contains("-insider", StringComparison.OrdinalIgnoreCase) == true));
            return;
        }

        foreach (var insiders in new[] { false, true })
        {
            var version = await _vsCodeCliRunner.GetVersionAsync(new VsCodeRunOptions { UseInsiders = insiders }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (version is not null)
            {
                // VS Code is a selection hint, not evidence that Copilot CLI is installed.
                // Keep its identity distinct so it cannot acquire CLI/App hook eligibility.
                context.AddDetection(new(AgentClientKind.VsCode, version.ToString(), IsInsiders: insiders));
                return;
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        foreach (var target in GetPluginTargets(request))
        {
            yield return target;
        }

        var copilotDirectory = CopilotPaths.GetConfigDirectory(_executionContext, _environment);
        if (request.Assets.Mcp)
        {
            // Agent Host reads root .mcp.json directly; no VS Code-specific file is needed.
            // https://code.visualstudio.com/docs/agents/reference/mcp-configuration
            yield return McpTarget(request.Scope is AgentConfigurationScope.Project
                ? Path.Combine(request.WorkspaceRoot.FullName, ".mcp.json")
                : Path.Combine(copilotDirectory, "mcp-config.json"), request.Scope);
        }

        AgentConfigurationTarget McpTarget(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.Mcp, [this], "mcpServers:aspire", async (root, context, cancellationToken) =>
            {
                var settings = await AgentConfigurationJson.ReadSettingsAsync(context, CopilotPaths.PluginSettings(request, _executionContext, _environment), cancellationToken);
                var managed = await AgentConfigurationJson.ReadSettingsAsync(context, CopilotPaths.ManagedSettings(_executionContext, _environment), cancellationToken);
                if (AspireMcpConfiguration.CheckPolicy(settings, managed, managedAllowlistOnly: true) is { } policy)
                {
                    return policy;
                }

                if (scope is AgentConfigurationScope.Project &&
                    await AspireMcpConfiguration.CheckOtherProjectFilesAsync(request.WorkspaceRoot, context, cancellationToken) is { } projectEntry)
                {
                    return projectEntry;
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

                var addCopilotDefaults = scope is AgentConfigurationScope.User &&
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
        if (request.Assets.AspireSkills)
        {
            yield return Target(request.Scope is AgentConfigurationScope.Project
                ? Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json")
                : Path.Combine(CopilotPaths.GetConfigDirectory(_executionContext, _environment), "settings.json"), request.Scope);
        }

        AgentConfigurationTarget Target(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.AspireSkills, [this], "plugins:aspire", async (root, context, cancellationToken) =>
                AspireSkillsPluginConfiguration.Apply(root, await AgentConfigurationJson.ReadSettingsAsync(
                    context, CopilotPaths.PluginSettings(request, _executionContext, _environment), cancellationToken)));
    }

    public AgentHookConfiguration? GetHookConfiguration(AgentInitRequest request)
        => request.Detections.Any(detection => detection.Client is AgentClientKind.CopilotApp or AgentClientKind.CopilotCli)
            ? new(
                Path.Combine(CopilotPaths.GetConfigDirectory(_executionContext, _environment), "hooks", "aspire-telemetry.json"),
                CopilotPaths.PluginSettings(request, _executionContext, _environment),
                CopilotPaths.ExistingHookSettings(request, _executionContext, _environment),
                ValidateHooks,
                ApplyHook)
            : null;

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
