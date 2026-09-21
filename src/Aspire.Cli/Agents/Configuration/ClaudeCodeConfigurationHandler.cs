// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Claude plugins use settings.json; native MCP belongs in .mcp.json and user .claude.json.
/// </summary>
internal sealed class ClaudeCodeConfigurationHandler(AgentConfigurationPaths paths) : IAgentConfigurationHandler
{
    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        if (!request.Clients.Contains(AgentClientKind.ClaudeCode))
        {
            yield break;
        }

        if (request.Assets.AspireSkills)
        {
            yield return PluginTarget(Path.Combine(request.WorkspaceRoot.FullName, ".claude", "settings.json"), AgentConfigurationScope.Project);
            yield return PluginTarget(Path.Combine(paths.ClaudeDirectory, "settings.json"), AgentConfigurationScope.User);
        }

        if (request.Assets.Mcp)
        {
            yield return McpTarget(Path.Combine(request.WorkspaceRoot.FullName, ".mcp.json"), AgentConfigurationScope.Project);
            yield return McpTarget(paths.ClaudeMcpFile, AgentConfigurationScope.User);
        }

        AgentConfigurationTarget PluginTarget(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.AspireSkills, [AgentClientKind.ClaudeCode], "plugins:aspire", async (root, context, cancellationToken) =>
                PluginConfiguration.Apply(root, await AgentConfigurationJson.ReadSettingsAsync(context, paths.PluginSettings(request, copilot: false), cancellationToken)));

        AgentConfigurationTarget McpTarget(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.Mcp, [AgentClientKind.ClaudeCode], "mcpServers:aspire", async (root, context, cancellationToken) =>
            {
                if (scope is AgentConfigurationScope.Project && McpConfiguration.UsesBareServers(root))
                {
                    // Copilot accepts a bare server map; Claude does not. Mixing a wrapper
                    // into that document would make Copilot stop seeing its other servers.
                    throw AgentConfigurationJson.Shape("mcpServers");
                }

                // Presence of managed-mcp.json gives the administrator exclusive control;
                // never add servers to it or pretend a user setting can override it.
                // https://code.claude.com/docs/en/mcp#managed-mcp-configuration
                if (await context.ReadOptionalAsync(Path.Combine(paths.ManagedDirectory(copilot: false), "managed-mcp.json"), cancellationToken) is not null)
                {
                    return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.PolicyBlocked);
                }

                var settings = (await AgentConfigurationJson.ReadSettingsAsync(context, paths.PluginSettings(request, copilot: false), cancellationToken)).ToList();
                if (await context.ReadOptionalAsync(paths.ClaudeMcpFile, cancellationToken) is { } state)
                {
                    settings.Add(state);
                    if (AgentConfigurationJson.OptionalObject(state, "projects") is { } projects)
                    {
                        foreach (var project in projects)
                        {
                            if (AgentConfigurationPath.Comparer.Equals(project.Key, request.WorkspaceRoot.FullName))
                            {
                                settings.Add(project.Value as JsonObject ?? throw AgentConfigurationJson.Shape("projects"));
                            }
                        }
                    }
                }

                if (await context.ReadOptionalAsync(Path.Combine(request.WorkspaceRoot.FullName, ".mcp.json"), cancellationToken) is { } projectMcp)
                {
                    settings.Add(projectMcp);
                }

                var managed = await AgentConfigurationJson.ReadSettingsAsync(context, paths.ManagedSettings(copilot: false), cancellationToken);
                if (McpConfiguration.CheckPolicy(settings, managed, managedAllowlistOnly: false) is { } policy)
                {
                    return policy;
                }

                foreach (var config in settings)
                {
                    if (AgentConfigurationJson.OptionalObject(config, "mcpServers") is { } servers && servers.ContainsKey(McpConfiguration.ServerName))
                    {
                        var existing = McpConfiguration.Apply(servers, "", commandArray: false, "stdio", copilot: false, bare: true);
                        if (existing.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Skipped)
                        {
                            return existing;
                        }

                        if (AgentConfigurationJson.OptionalObject(root, "mcpServers")?.ContainsKey(McpConfiguration.ServerName) is not true &&
                            !McpConfiguration.IsDefaultEntry(servers[McpConfiguration.ServerName]!.AsObject(), commandArray: false))
                        {
                            return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.ExistingMcpCustomization);
                        }
                    }
                }

                return McpConfiguration.Apply(root, "mcpServers", commandArray: false, "stdio", copilot: false);
            });
    }
}
