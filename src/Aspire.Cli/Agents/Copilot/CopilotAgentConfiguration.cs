// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Copilot CLI/App and the supported VS Code Agent Host share plugin settings, not native MCP files.
/// </summary>
internal static class CopilotAgentConfiguration
{
    internal const string HookEventName = "postToolUse";

    public static IEnumerable<AgentClientDescriptor> GetClients()
    {
        yield return new(AgentClientKind.CopilotCli, "copilot-cli", "GitHub Copilot CLI", GetTargets);
        yield return new(AgentClientKind.CopilotApp, "copilot-app", "GitHub Copilot App", GetTargets);
    }

    private static IEnumerable<AgentConfigurationTarget> GetTargets(
        AgentInitRequest request,
        CliExecutionContext executionContext,
        IEnvironment environment)
    {
        foreach (var target in GetPluginTargets(request, executionContext, environment))
        {
            yield return target;
        }

        var copilotDirectory = CopilotPaths.GetConfigDirectory(executionContext, environment);
        var mcpClients = request.Clients.Where(client => client is AgentClientKind.CopilotCli or AgentClientKind.CopilotApp).Distinct().ToArray();
        if (request.Assets.Mcp && mcpClients.Length > 0)
        {
            var rootMcp = Path.Combine(request.WorkspaceRoot.FullName, ".mcp.json");
            // Copilot accepts stdio and the root .mcp.json schema used by Claude. Share that
            // physical entry when applicable; do not create an ineffective second overlay.
            // https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers
            var projectPath = File.Exists(rootMcp) || request.Clients.Contains(AgentClientKind.ClaudeCode)
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
                            return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.ExistingMcpCustomization);
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
        var clients = request.Clients.Where(client => client is AgentClientKind.CopilotCli or AgentClientKind.CopilotApp or AgentClientKind.VsCode).Distinct().ToArray();
        if (request.Assets.AspireSkills && clients.Length > 0)
        {
            yield return Target(Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json"), AgentConfigurationScope.Project);
            yield return Target(Path.Combine(CopilotPaths.GetConfigDirectory(executionContext, environment), "settings.json"), AgentConfigurationScope.User);
        }

        AgentConfigurationTarget Target(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.AspireSkills, clients, "plugins:aspire", async (root, context, cancellationToken) =>
                PluginConfiguration.Apply(root, await AgentConfigurationJson.ReadSettingsAsync(
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
