// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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

    /// <summary>
    /// <c>PostToolUse</c> <c>matcher</c> for the Aspire MCP tools. Skills and reads use <see cref="TelemetryHookSkillIf"/>
    /// and <see cref="TelemetryHookReadIf"/>.
    /// </summary>
    /// <remarks>
    /// Claude Code matches unanchored, so the explicit anchors keep each matcher from matching other tool names
    /// (https://code.claude.com/docs/en/hooks#matcher-patterns).
    /// </remarks>
    internal static string TelemetryHookMcpMatcher { get; } = $"^(?:{AgentTelemetryHook.McpMatcher})$";

    internal const string TelemetryHookSkillMatcher = "^Skill$";

    /// <summary>
    /// Handler <c>if</c> rule that limits Skill hooks to Aspire skills.
    /// </summary>
    /// <remarks>
    /// Without a space before <c>*</c>, the rule is a plain prefix match, the same way <c>Bash(ls*)</c> matches <c>lsof</c>
    /// (https://code.claude.com/docs/en/permissions#wildcard-patterns). Claude matches Skill rules against the skill's own
    /// name and the name in Claude's invocation (https://code.claude.com/docs/en/skills#restrict-claudes-skill-access).
    /// Both start with <c>aspire</c> for every canonical skill, whether invoked as <c>aspire-init</c> or through the plugin
    /// as <c>aspire:aspire-init</c>.
    /// </remarks>
    internal const string TelemetryHookSkillIf = "Skill(aspire*)";

    internal const string TelemetryHookReadMatcher = "^Read$";

    /// <summary>
    /// Handler <c>if</c> rule that limits Read hooks to files under an Aspire skill directory.
    /// </summary>
    /// <remarks>
    /// Every canonical skill name starts with <c>aspire</c>. <c>//**/</c> matches at any absolute depth, so this covers
    /// plugin, user, and project skill installs (https://code.claude.com/docs/en/hooks#filter-by-tool-name-and-arguments-with-the-if-field).
    /// </remarks>
    internal const string TelemetryHookReadIf = "Read(//**/skills/aspire*/**)";

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

    public override string ToString() => Id;

    public async Task<TelemetryHookStatus> ConfigureTelemetryHookAsync(
        AgentConfigurationScope scope, DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        // Project hooks go in the shared settings.json, not settings.local.json, so they can be committed.
        // https://code.claude.com/docs/en/settings#settings-files
        var settingsPath = GetIntegrationConfigPath(scope, workspaceRoot);
        var edit = new AgentConfiguration();
        byte[]? bytes;
        try
        {
            bytes = await edit.ReadBytesForEditAsync(settingsPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to read Claude settings at {Path}.", settingsPath);
            return TelemetryHookStatus.WriteFailed;
        }

        JsonObject settings;
        if (bytes is not null)
        {
            using var reader = new StreamReader(new MemoryStream(bytes));
            var content = await reader.ReadToEndAsync(cancellationToken);
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(content);
            }
            catch (JsonException ex)
            {
                // Preserve malformed settings rather than replacing content we cannot merge.
                logger.LogDebug(ex, "Claude settings at {Path} contained malformed JSON; skipping hook registration.", settingsPath);
                return TelemetryHookStatus.MalformedConfig;
            }

            switch (parsed)
            {
                case null:
                    settings = new JsonObject();
                    break;
                case JsonObject existing:
                    settings = existing;
                    break;
                default:
                    return TelemetryHookStatus.UnexpectedConfigShape;
            }
        }
        else
        {
            settings = new JsonObject();
        }

        JsonObject hooks;
        if (settings.TryGetPropertyValue("hooks", out var hooksNode))
        {
            if (hooksNode is not JsonObject hooksObject)
            {
                return TelemetryHookStatus.UnexpectedConfigShape;
            }
            hooks = hooksObject;
        }
        else
        {
            hooks = new JsonObject();
            settings["hooks"] = hooks;
        }

        JsonArray postToolUse;
        if (hooks.TryGetPropertyValue("PostToolUse", out var postToolUseNode))
        {
            if (postToolUseNode is not JsonArray postToolUseArray)
            {
                return TelemetryHookStatus.UnexpectedConfigShape;
            }
            postToolUse = postToolUseArray;
        }
        else
        {
            postToolUse = new JsonArray();
            hooks["PostToolUse"] = postToolUse;
        }

        RemoveExistingAspireHooks(postToolUse);
        // Run directly, without a shell, and only for tools or skill file reads the classifier can
        // track so Claude does not launch the CLI on every tool call or read.
        // https://docs.claude.com/en/docs/claude-code/hooks
        postToolUse.Add(CreateHookGroup(TelemetryHookSkillMatcher, TelemetryHookSkillIf, scope));
        postToolUse.Add(CreateHookGroup(TelemetryHookMcpMatcher, condition: null, scope));
        postToolUse.Add(CreateHookGroup(TelemetryHookReadMatcher, TelemetryHookReadIf, scope));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await edit.PublishAsync(settingsPath, settings, cancellationToken);
            return TelemetryHookStatus.Configured;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to write Claude settings at {Path}.", settingsPath);
            return TelemetryHookStatus.WriteFailed;
        }
    }

    private static JsonNode CreateHookGroup(string matcher, string? condition, AgentConfigurationScope scope)
    {
        var (command, args) = AgentTelemetryHook.GetCommand(
            AgentTelemetryProtocol.HookOptionName,
            AgentTelemetryProtocol.SourceOptionName, ClientId,
            AgentTelemetryProtocol.ScopeOptionName, scope is AgentConfigurationScope.Project ? AgentTelemetryProtocol.ProjectScope : AgentTelemetryProtocol.UserScope);
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["args"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
            ["timeout"] = 30,
        };
        if (condition is not null)
        {
            hook["if"] = condition;
        }

        return new JsonObject
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(hook),
        };
    }

    private static void RemoveExistingAspireHooks(JsonArray postToolUse)
    {
        // Remove individual Aspire hooks, preserving user hooks that share the same matcher group.
        for (var groupIndex = postToolUse.Count - 1; groupIndex >= 0; groupIndex--)
        {
            if (postToolUse[groupIndex] is not JsonObject group
                || group["hooks"] is not JsonArray hooks)
            {
                continue;
            }
            for (var hookIndex = hooks.Count - 1; hookIndex >= 0; hookIndex--)
            {
                if (hooks[hookIndex] is JsonObject hook && AgentTelemetryHook.IsHookInvocation(hook["command"], hook["args"]))
                {
                    hooks.RemoveAt(hookIndex);
                }
            }
            if (hooks.Count == 0)
            {
                postToolUse.RemoveAt(groupIndex);
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
