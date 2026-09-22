// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Discovers and configures Copilot CLI/App, including plugin settings shared with the supported VS Code Agent Host.
/// </summary>
internal sealed class CopilotAgentEnvironmentScanner(
    ICopilotCliRunner copilotCliRunner,
    ICopilotAppInstallationDetector copilotAppInstallationDetector,
    CliExecutionContext executionContext,
    IEnvironment environment,
    ILogger<CopilotAgentEnvironmentScanner> logger) : IAgentClientEnvironment
{
    internal const string CliClientId = "copilot-cli";
    internal const string AppClientId = "copilot-app";
    internal const string HookEventName = "postToolUse";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentClientDetection>> ScanAsync(IReadOnlyList<AgentClient> clients, DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("Starting GitHub Copilot environment scan");

        var detections = new List<AgentClientDetection>();
        if (copilotAppInstallationDetector.GetInstallationMarker() is { } installationMarker)
        {
            logger.LogDebug("Detected GitHub Copilot App using installation marker {Marker}", installationMarker);
            detections.Add(new AgentClientDetection(clients.Single(client => client.Id == AppClientId), Version: null, IsInsiders: false));
        }

        // VS Code can supply an interactive Copilot installation shim. Do not invoke it during
        // discovery, where an installation prompt could hang the enclosing command.
        if (environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            logger.LogDebug("Detected VS Code terminal environment. Skipping the Copilot CLI version probe.");
            detections.Add(new AgentClientDetection(clients.Single(client => client.Id == CliClientId), Version: null, IsInsiders: false));
        }
        else
        {
            var version = await copilotCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                logger.LogDebug("Found GitHub Copilot CLI version: {Version}", version);
                detections.Add(new AgentClientDetection(clients.Single(client => client.Id == CliClientId), version.ToString(), IsInsiders: false));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return detections.AsReadOnly();
    }

    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        foreach (var target in GetPluginTargets(request, executionContext, environment))
        {
            yield return target;
        }

        var copilotDirectory = CopilotPaths.GetConfigDirectory(executionContext, environment);
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
                var settings = await AgentConfigurationJson.ReadSettingsAsync(context, CopilotPaths.PluginSettings(request, executionContext, environment), cancellationToken);
                var managed = await AgentConfigurationJson.ReadSettingsAsync(context, CopilotPaths.ManagedSettings(executionContext, environment), cancellationToken);
                if (McpConfiguration.CheckPolicy(settings, managed, managedAllowlistOnly: true) is { } policy)
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
                    if (McpConfiguration.CheckPolicy([other]) is { } disabled)
                    {
                        return disabled;
                    }

                    var servers = McpConfiguration.UsesBareServers(other) ? other : AgentConfigurationJson.OptionalObject(other, "mcpServers");
                    if (servers?.ContainsKey(McpConfiguration.ServerName) is true)
                    {
                        var existing = McpConfiguration.Apply(servers, "", commandArray: false, "stdio", bare: true);
                        if (existing.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Skipped)
                        {
                            return existing;
                        }

                        var ownServers = McpConfiguration.UsesBareServers(root) ? root : AgentConfigurationJson.OptionalObject(root, "mcpServers");
                        if (ownServers?.ContainsKey(McpConfiguration.ServerName) is not true &&
                            !McpConfiguration.IsDefaultEntry(servers[McpConfiguration.ServerName]!.AsObject(), commandArray: false))
                        {
                            return AgentConfigurationEdit.Skipped(AgentCommandStrings.Configuration_ExistingMcpCustomization);
                        }
                    }
                }

                var addCopilotDefaults = copilotEnvironment &&
                    AgentConfigurationJson.OptionalObject(root, "mcpServers")?.ContainsKey(McpConfiguration.ServerName) is not true;
                var edit = McpConfiguration.Apply(root, "mcpServers", commandArray: false, "stdio",
                    bare: scope is AgentConfigurationScope.Project && McpConfiguration.UsesBareServers(root));
                if (addCopilotDefaults && edit.Status is AgentConfigurationStatus.Configured)
                {
                    // Copilot does not inherit arbitrary environment variables for local MCP
                    // servers. Preserve Aspire's existing DOTNET_ROOT pass-through contract.
                    var server = root["mcpServers"]![McpConfiguration.ServerName]!.AsObject();
                    server["env"] = new JsonObject { ["DOTNET_ROOT"] = "${DOTNET_ROOT}" };
                    server["tools"] = new JsonArray("*");
                }

                return edit;
            });
    }

    public static IEnumerable<AgentConfigurationTarget> GetPluginTargets(
        AgentInitRequest request,
        CliExecutionContext executionContext,
        IEnvironment environment)
    {
        var clients = request.Clients.Where(client => client.Environment is CopilotAgentEnvironmentScanner or VsCodeAgentEnvironmentScanner).ToArray();
        if (request.Assets.AspireSkills && clients.Length > 0)
        {
            yield return Target(Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json"), AgentConfigurationScope.Project);
            yield return Target(Path.Combine(CopilotPaths.GetConfigDirectory(executionContext, environment), "settings.json"), AgentConfigurationScope.User);
        }

        AgentConfigurationTarget Target(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.AspireSkills, clients, "plugins:aspire", async (root, context, cancellationToken) =>
                AspireSkillsPluginConfiguration.Apply(root, await AgentConfigurationJson.ReadSettingsAsync(
                    context, CopilotPaths.PluginSettings(request, executionContext, environment), cancellationToken)));
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
