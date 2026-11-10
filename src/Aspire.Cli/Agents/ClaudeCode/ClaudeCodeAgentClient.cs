// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.ClaudeCode;

internal sealed class ClaudeCodeAgentClient(
    CliExecutionContext context, IEnvironment environment, ILogger<ClaudeCodeAgentClient> logger)
    : IAgentClient
{
    public const string ClientId = "claude";

    private static readonly McpConfigFileHelper.Configuration s_mcp = new() { ServersPropertyName = "mcpServers" };

    public string Id => ClientId;
    public string DisplayName { get; } = AgentCommandStrings.Environment_ClaudeCode;
    public AgentEnvironment Environments => AgentEnvironment.ClaudeCode;

    public string GetSkillBaseDirectory(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => Path.Combine(scope is AgentConfigurationScope.Project
            ? Path.Combine(workspaceRoot.FullName, ClaudeCodePaths.ConfigDirectoryName)
            : ClaudeCodePaths.GetConfigDirectory(context, environment), "skills");

    public IEnumerable<string> GetLocalSkillDirectories(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        yield return GetSkillBaseDirectory(scope, workspaceRoot);
    }

    public bool HasIntegrationRegistration(
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, IReadOnlyList<SkillSource> sources)
        => AspireSkillsRemoteConfiguration.HasPluginRegistration(
            GetSettingsFiles(workspaceRoot, scope,
                AgentConfiguration.EnumerateJsonFiles(Path.Combine(ManagedSettingsDirectory, "managed-settings.d"))), sources);

    public string GetIntegrationConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        var directory = scope is AgentConfigurationScope.Project
            ? Path.Combine(workspaceRoot.FullName, ClaudeCodePaths.ConfigDirectoryName)
            : ClaudeCodePaths.GetConfigDirectory(context, environment);

        return Path.Combine(directory, "settings.json");
    }

    public Task UpdateIntegrationConfigAsync(
        JsonObject config, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, IReadOnlyList<SkillSource> sources, CancellationToken cancellationToken)
        => AspireSkillsRemoteConfiguration.ApplyPluginAsync(config,
            GetSettingsFiles(workspaceRoot, scope,
                edit.GetJsonFilesForEdit(Path.Combine(ManagedSettingsDirectory, "managed-settings.d"))),
            GetIntegrationConfigPath(scope, workspaceRoot), edit, sources, cancellationToken);

    public string GetMcpConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => scope is AgentConfigurationScope.Project ? Path.Combine(workspaceRoot.FullName, ".mcp.json")
            : ClaudeCodePaths.GetGlobalMcpConfigPath(context, environment);

    public IEnumerable<(string Path, bool ConfigureServer)> GetMcpConfigPaths(
        AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => [(GetMcpConfigPath(scope, workspaceRoot), true)];

    public async Task<(JsonObject Config, string Path)> ReadMcpConfigAsync(
        string configFilePath, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken)
        => (await McpConfigFileHelper.ReadConfigAsync(configFilePath, s_mcp.JsonDocumentOptions, edit, cancellationToken), configFilePath);

    public (bool Repaired, bool Added) UpdateMcpConfig(
        JsonObject config, string configFilePath, bool configureServer, AgentConfigurationScope scope)
    {
        var repaired = McpConfigFileHelper.UpdateDeprecatedCommand(config, s_mcp.ServersPropertyName);
        var added = configureServer && McpConfigFileHelper.ConfigureMcpServer(config, s_mcp, scope, configFilePath);

        return (repaired, added);
    }

    public bool SupportsTelemetryHooks => true;

    public async Task<TelemetryHookConfigurationResult> ConfigureTelemetryHookAsync(CancellationToken cancellationToken)
    {
        var reason = await WriteTelemetryHookAsync(cancellationToken);

        return reason is { } skipReason
            ? new TelemetryHookConfigurationResult([], [new TelemetryHookSkip(this, skipReason)])
            : new TelemetryHookConfigurationResult([this], []);
    }

    public override string ToString() => Id;

    private Task<TelemetryHookSkipReason?> WriteTelemetryHookAsync(CancellationToken cancellationToken)
    {
        var settingsPath = Path.Combine(ClaudeCodePaths.GetConfigDirectory(context, environment), "settings.json");
        return TelemetryHookSettings.UpdateAsync(settingsPath, logger, hooks =>
        {
            if (!TelemetryHookSettings.TryGetEventArray(hooks, "PostToolUse", out var postToolUse))
            {
                return false;
            }

            // Run directly, without a shell, and only for tools or skill file reads the classifier can
            // track so Claude does not launch the CLI on every tool call or read.
            // https://code.claude.com/docs/en/hooks
            RemoveExistingAspireHooks(postToolUse);
            AddAspireHook(postToolUse, AgentTelemetryHook.ClaudeToolMatcher, condition: null);
            AddAspireHook(postToolUse, AgentTelemetryHook.ClaudeReadMatcher, AgentTelemetryHook.ClaudeReadIf);
            return true;
        }, cancellationToken);
    }

    private static void AddAspireHook(JsonArray groups, string matcher, string? condition)
    {
        var (command, args) = AgentTelemetryHook.GetCommand(AgentTelemetryProtocol.HookOptionName);
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["args"] = TelemetryHookSettings.CreateArgs(args),
            ["timeout"] = 30,
        };
        if (condition is not null)
        {
            hook["if"] = condition;
        }
        groups.Add((JsonNode)new JsonObject
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(hook),
        });
    }

    private static void RemoveExistingAspireHooks(JsonArray groups)
    {
        // Remove individual Aspire hooks, preserving user hooks that share the same matcher group.
        for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
        {
            if (groups[groupIndex] is not JsonObject group
                || group["hooks"] is not JsonArray hooks)
            {
                continue;
            }
            for (var hookIndex = hooks.Count - 1; hookIndex >= 0; hookIndex--)
            {
                if (TelemetryHookSettings.IsAspireHook(hooks[hookIndex], "command"))
                {
                    hooks.RemoveAt(hookIndex);
                }
            }
            if (hooks.Count == 0)
            {
                groups.RemoveAt(groupIndex);
            }
        }
    }

    private string ManagedSettingsDirectory
        => AgentPath.GetManagedSettingsDirectory(context, environment, "ClaudeCode", "claude-code");

    private IEnumerable<string> GetSettingsFiles(
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, IEnumerable<string> fragments)
    {
        yield return Path.Combine(ClaudeCodePaths.GetConfigDirectory(context, environment), "settings.json");
        if (scope is AgentConfigurationScope.Project)
        {
            yield return Path.Combine(workspaceRoot.FullName, ".claude", "settings.json");
            yield return Path.Combine(workspaceRoot.FullName, ".claude", "settings.local.json");
        }
        yield return Path.Combine(ManagedSettingsDirectory, "managed-settings.json");
        foreach (var path in fragments)
        {
            yield return path;
        }
    }
}
