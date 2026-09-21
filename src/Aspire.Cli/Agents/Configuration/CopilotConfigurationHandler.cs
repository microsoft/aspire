// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Copilot CLI/App and the supported VS Code Agent Host share plugin settings, not native MCP files.
/// </summary>
internal sealed class CopilotConfigurationHandler(AgentConfigurationPaths paths) : IAgentConfigurationHandler
{
    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        var pluginClients = request.Clients.Where(client => client is AgentClientKind.CopilotCli or AgentClientKind.CopilotApp or AgentClientKind.VsCode).Distinct().ToArray();
        if (request.Assets.AspireSkills && pluginClients.Length > 0)
        {
            yield return PluginTarget(Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json"), AgentConfigurationScope.Project);
            yield return PluginTarget(Path.Combine(paths.CopilotDirectory, "settings.json"), AgentConfigurationScope.User);
        }

        var mcpClients = pluginClients.Where(client => client is not AgentClientKind.VsCode).ToArray();
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
            yield return McpTarget(Path.Combine(paths.CopilotDirectory, "mcp-config.json"), AgentConfigurationScope.User, copilotEnvironment: true);
        }

        AgentConfigurationTarget PluginTarget(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.AspireSkills, pluginClients, "plugins:aspire", async (root, context, cancellationToken) =>
                PluginConfiguration.Apply(root, await AgentConfigurationJson.ReadSettingsAsync(context, paths.PluginSettings(request, copilot: true), cancellationToken)));

        AgentConfigurationTarget McpTarget(string path, AgentConfigurationScope scope, bool copilotEnvironment)
            => new(path, scope, AgentAssetKind.Mcp, mcpClients, "mcpServers:aspire", async (root, context, cancellationToken) =>
            {
                var settings = await AgentConfigurationJson.ReadSettingsAsync(context, paths.PluginSettings(request, copilot: true), cancellationToken);
                var managed = await AgentConfigurationJson.ReadSettingsAsync(context, paths.ManagedSettings(copilot: true), cancellationToken);
                if (McpConfiguration.CheckPolicy(settings, managed, managedAllowlistOnly: true) is { } policy)
                {
                    return policy;
                }

                foreach (var other in await AgentConfigurationJson.ReadSettingsAsync(context,
                    [
                        Path.Combine(paths.CopilotDirectory, "mcp-config.json"),
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
                        var existing = McpConfiguration.Apply(servers, "", commandArray: false, "stdio", copilot: false, bare: true);
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

                return McpConfiguration.Apply(root, "mcpServers", commandArray: false, "stdio", copilotEnvironment,
                    bare: scope is AgentConfigurationScope.Project && McpConfiguration.UsesBareServers(root));
            });
    }
}
