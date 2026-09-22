// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.ClaudeCode;

/// <summary>
/// Discovers Claude Code and supplies its native plugin, MCP, and hook configuration.
/// </summary>
/// <param name="claudeCodeCliRunner">The Claude Code CLI runner for checking if Claude Code is installed.</param>
/// <param name="executionContext">The CLI execution context for resolving workspace and user configuration paths.</param>
/// <param name="environment">The environment abstraction for reading environment variables.</param>
/// <param name="logger">The logger for diagnostic output.</param>
internal sealed class ClaudeCodeAgentEnvironmentScanner(
    IClaudeCodeCliRunner claudeCodeCliRunner,
    CliExecutionContext executionContext,
    IEnvironment environment,
    ILogger<ClaudeCodeAgentEnvironmentScanner> logger) : IAgentClientEnvironment
{
    internal const string ClientId = "claude-code";
    internal const string HookEventName = "PostToolUse";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentClientDetection>> ScanAsync(IReadOnlyList<AgentClient> clients, DirectoryInfo workingDirectory, DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("Starting Claude Code environment scan in directory: {WorkingDirectory}", workingDirectory.FullName);

        var hasProjectConfiguration = HasProjectConfiguration(workingDirectory, workspaceRoot);
        var version = await claudeCodeCliRunner.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (hasProjectConfiguration || version is not null)
        {
            logger.LogDebug("Detected Claude Code with version: {Version}", version);
            return Array.AsReadOnly<AgentClientDetection>(
            [
                new(clients.Single(client => client.Id == ClientId), version?.ToString(), IsInsiders: false)
            ]);
        }

        return Array.AsReadOnly<AgentClientDetection>([]);
    }

    /// <summary>
    /// Checks for .claude or .mcp.json within the workspace boundary, excluding user-level home configuration.
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from.</param>
    /// <param name="repositoryRoot">The workspace root to use as the boundary for searches.</param>
    private bool HasProjectConfiguration(DirectoryInfo startDirectory, DirectoryInfo repositoryRoot)
        => AgentPath.ProjectDirectories(startDirectory, repositoryRoot).Any(directory =>
            Path.GetRelativePath(executionContext.HomeDirectory.FullName, directory.FullName) != "." &&
            (Directory.Exists(Path.Combine(directory.FullName, ".claude")) ||
             File.Exists(Path.Combine(directory.FullName, ".mcp.json"))));

    /// <inheritdoc />
    public IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request)
    {
        var client = request.Clients.Single(client => client.Environment == this);
        var mcpFile = GetMcpFile(executionContext, environment);
        if (request.Assets.AspireSkills)
        {
            yield return PluginTarget(Path.Combine(request.WorkspaceRoot.FullName, ".claude", "settings.json"), AgentConfigurationScope.Project);
            yield return PluginTarget(Path.Combine(GetConfigDirectory(executionContext, environment), "settings.json"), AgentConfigurationScope.User);
        }

        if (request.Assets.Mcp)
        {
            yield return McpTarget(Path.Combine(request.WorkspaceRoot.FullName, ".mcp.json"), AgentConfigurationScope.Project);
            yield return McpTarget(mcpFile, AgentConfigurationScope.User);
        }

        AgentConfigurationTarget PluginTarget(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.AspireSkills, [client], "plugins:aspire", async (root, context, cancellationToken) =>
                AspireSkillsPluginConfiguration.Apply(root, await AgentConfigurationJson.ReadSettingsAsync(context, PluginSettings(request, executionContext, environment), cancellationToken)));

        AgentConfigurationTarget McpTarget(string path, AgentConfigurationScope scope)
            => new(path, scope, AgentAssetKind.Mcp, [client], "mcpServers:aspire", async (root, context, cancellationToken) =>
            {
                if (scope is AgentConfigurationScope.Project && AspireMcpConfiguration.UsesBareServers(root))
                {
                    // Copilot accepts a bare server map; Claude does not. Mixing a wrapper
                    // into that document would make Copilot stop seeing its other servers.
                    throw AgentConfigurationJson.Shape("mcpServers");
                }

                // Presence of managed-mcp.json gives the administrator exclusive control;
                // never add servers to it or pretend a user setting can override it.
                // https://code.claude.com/docs/en/mcp#managed-mcp-configuration
                if (await context.ReadOptionalAsync(Path.Combine(GetManagedDirectory(executionContext, environment), "managed-mcp.json"), cancellationToken) is not null)
                {
                    return AgentConfigurationEdit.Blocked(AgentCommandStrings.Configuration_PolicyBlocked);
                }

                var settings = (await AgentConfigurationJson.ReadSettingsAsync(context, PluginSettings(request, executionContext, environment), cancellationToken)).ToList();
                if (await context.ReadOptionalAsync(mcpFile, cancellationToken) is { } state)
                {
                    settings.Add(state);
                    if (AgentConfigurationJson.OptionalObject(state, "projects") is { } projects)
                    {
                        foreach (var project in projects)
                        {
                            if (AgentPath.Comparer.Equals(project.Key, request.WorkspaceRoot.FullName))
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

                var managed = await AgentConfigurationJson.ReadSettingsAsync(context, ManagedSettings(executionContext, environment), cancellationToken);
                if (AspireMcpConfiguration.CheckPolicy(settings, managed, managedAllowlistOnly: false) is { } policy)
                {
                    return policy;
                }

                foreach (var config in settings)
                {
                    if (AspireMcpConfiguration.CheckExistingEntry(AgentConfigurationJson.OptionalObject(config, "mcpServers"), commandArray: false,
                        () => AgentConfigurationJson.OptionalObject(root, "mcpServers")) is { } existing)
                    {
                        return existing;
                    }
                }

                return AspireMcpConfiguration.Apply(root, "mcpServers", commandArray: false, "stdio");
            });
    }

    public static string GetConfigDirectory(CliExecutionContext executionContext, IEnvironment environment)
        => AgentPath.GetOverride("CLAUDE_CONFIG_DIR", executionContext, environment) ?? Path.Combine(executionContext.HomeDirectory.FullName, ".claude");

    // CLAUDE_CONFIG_DIR moves the state file into the directory, unlike ~/.claude.json.
    // https://code.claude.com/docs/en/env-vars
    // https://github.com/anthropics/claude-code/issues/79275
    public static string GetMcpFile(CliExecutionContext executionContext, IEnvironment environment)
        => AgentPath.GetOverride("CLAUDE_CONFIG_DIR", executionContext, environment) is { } directory
            ? Path.Combine(directory, ".claude.json")
            : Path.Combine(executionContext.HomeDirectory.FullName, ".claude.json");

    // Only personal skills follow CLAUDE_CONFIG_DIR, not project skills.
    // https://code.claude.com/docs/en/claude-directory
    public static string GetSkillDirectory(DirectoryInfo workspaceRoot, AgentConfigurationScope scope, CliExecutionContext executionContext, IEnvironment environment)
        => scope is AgentConfigurationScope.User
            ? Path.Combine(GetConfigDirectory(executionContext, environment), "skills")
            : Path.Combine(workspaceRoot.FullName, ".claude", "skills");

    public static IEnumerable<string> ProjectSettings(DirectoryInfo workspaceRoot)
    {
        yield return Path.Combine(workspaceRoot.FullName, ".claude", "settings.json");
        yield return Path.Combine(workspaceRoot.FullName, ".claude", "settings.local.json");
    }

    public static IEnumerable<string> PluginSettings(AgentInitRequest request, CliExecutionContext executionContext, IEnvironment environment)
    {
        yield return Path.Combine(GetConfigDirectory(executionContext, environment), "settings.json");
        foreach (var path in ProjectSettings(request.WorkspaceRoot).Concat(ManagedSettings(executionContext, environment)))
        {
            yield return path;
        }
    }

    // https://code.claude.com/docs/en/managed-settings
    public static string GetManagedDirectory(CliExecutionContext executionContext, IEnvironment environment)
        => AgentPath.GetManagedDirectory(executionContext, environment, "ClaudeCode", "claude-code");

    public static IEnumerable<string> ManagedSettings(CliExecutionContext executionContext, IEnvironment environment)
    {
        var directory = GetManagedDirectory(executionContext, environment);
        yield return Path.Combine(directory, "managed-settings.json");
        var fragments = Path.Combine(directory, "managed-settings.d");
        if (Directory.Exists(fragments))
        {
            foreach (var file in Directory.EnumerateFiles(fragments, "*.json").Order(StringComparer.Ordinal))
            {
                if (!Path.GetFileName(file).StartsWith('.'))
                {
                    yield return file;
                }
            }
        }
    }

    public static void ValidateHooks(JsonObject root)
    {
        var hooks = AgentConfigurationJson.OptionalObject(root, "hooks");
        if (hooks is null || !hooks.TryGetPropertyValue(HookEventName, out var entries))
        {
            return;
        }

        if (entries is not JsonArray array || array.Any(entry => entry is not JsonObject))
        {
            throw AgentConfigurationJson.Shape($"hooks.{HookEventName}");
        }

        foreach (var group in array.OfType<JsonObject>())
        {
            if (group["hooks"] is not JsonArray inner || inner.Any(entry => entry is not JsonObject) ||
                (group.ContainsKey("matcher") && AgentConfigurationJson.String(group["matcher"]) is null))
            {
                throw AgentConfigurationJson.Shape($"hooks.{HookEventName}");
            }
        }
    }

    public static void ApplyHook(JsonObject root, TelemetryHookScripts scripts, Func<JsonNode?, bool> isAspireHook)
    {
        var hooks = AgentConfigurationJson.Object(root, "hooks");
        var groups = hooks[HookEventName] as JsonArray ?? new JsonArray();
        if (!hooks.ContainsKey(HookEventName))
        {
            hooks[HookEventName] = groups;
        }

        // Retain the established exec form and shipped script/event/opt-out contract.
        // https://code.claude.com/docs/en/hooks#command-hook-fields
        var desired = new JsonObject
        {
            ["type"] = "command",
            ["command"] = OperatingSystem.IsWindows() ? "pwsh" : "bash",
            ["args"] = OperatingSystem.IsWindows()
                ? new JsonArray("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scripts.PowerShellScriptPath)
                : new JsonArray(scripts.ShellScriptPath),
            ["timeout"] = TelemetryHookConfigurator.HookTimeoutSeconds
        };

        var owned = groups.OfType<JsonObject>()
            .SelectMany(group => ((JsonArray)group["hooks"]!).Where(isAspireHook).Select(hook => (Group: group, Hook: hook)))
            .ToArray();
        if (owned.Length == 1 && AgentConfigurationJson.String(owned[0].Group["matcher"]) == "*" &&
            JsonNode.DeepEquals(owned[0].Hook, desired))
        {
            return;
        }

        foreach (var (group, hook) in owned)
        {
            var entries = (JsonArray)group["hooks"]!;
            entries.Remove(hook);
            if (entries.Count == 0)
            {
                groups.Remove(group);
            }
        }

        groups.Add((JsonNode)new JsonObject { ["matcher"] = "*", ["hooks"] = new JsonArray(desired) });
    }
}
