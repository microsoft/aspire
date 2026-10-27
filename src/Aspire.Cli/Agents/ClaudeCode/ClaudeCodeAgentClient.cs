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
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, CancellationToken cancellationToken)
        => AspireSkillsRemoteConfiguration.HasPluginRegistration(
            GetSettingsFiles(workspaceRoot, scope,
                AgentConfiguration.EnumerateJsonFiles(Path.Combine(ManagedSettingsDirectory, "managed-settings.d"))), cancellationToken);

    public string GetIntegrationConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        var directory = scope is AgentConfigurationScope.Project
            ? Path.Combine(workspaceRoot.FullName, ClaudeCodePaths.ConfigDirectoryName)
            : ClaudeCodePaths.GetConfigDirectory(context, environment);

        return Path.Combine(directory, "settings.json");
    }

    public Task UpdateIntegrationConfigAsync(
        JsonObject config, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken)
        => AspireSkillsRemoteConfiguration.ApplyPluginAsync(config,
            GetSettingsFiles(workspaceRoot, scope,
                edit.GetJsonFilesForEdit(Path.Combine(ManagedSettingsDirectory, "managed-settings.d"))),
            GetIntegrationConfigPath(scope, workspaceRoot), edit, cancellationToken);

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

    public async Task<TelemetryHookConfigurationResult> ConfigureTelemetryHooksAsync(
        AgentEnvironment detectedEnvironments, CancellationToken cancellationToken)
    {
        if (!detectedEnvironments.HasFlag(AgentEnvironment.ClaudeCode))
        {
            return new TelemetryHookConfigurationResult(AgentEnvironment.None, []);
        }
        var reason = await ConfigureTelemetryHookAsync(cancellationToken);

        return reason is { } skipReason
            ? new TelemetryHookConfigurationResult(AgentEnvironment.None,
                [new TelemetryHookSkip(AgentEnvironment.ClaudeCode, skipReason)])
            : new TelemetryHookConfigurationResult(AgentEnvironment.ClaudeCode, []);
    }

    public override string ToString() => Id;

    private async Task<TelemetryHookSkipReason?> ConfigureTelemetryHookAsync(CancellationToken cancellationToken)
    {
        var claudeDirectory = ClaudeCodePaths.GetConfigDirectory(context, environment);
        var settingsPath = Path.Combine(claudeDirectory, "settings.json");
        var edit = new AgentConfiguration();
        byte[]? bytes;
        try
        {
            bytes = await edit.ReadBytesForEditAsync(settingsPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to read Claude settings at {Path}.", settingsPath);
            return TelemetryHookSkipReason.WriteFailed;
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
                return TelemetryHookSkipReason.MalformedConfig;
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
                    return TelemetryHookSkipReason.UnexpectedConfigShape;
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
                return TelemetryHookSkipReason.UnexpectedConfigShape;
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
                return TelemetryHookSkipReason.UnexpectedConfigShape;
            }
            postToolUse = postToolUseArray;
        }
        else
        {
            postToolUse = new JsonArray();
            hooks["PostToolUse"] = postToolUse;
        }

        RemoveExistingAspireHooks(postToolUse);
        // Register all-tool coverage with direct execution, without a shell on each invocation.
        // https://docs.claude.com/en/docs/claude-code/hooks
        var (command, args) = AgentTelemetryHook.GetCommand(AgentTelemetryProtocol.HookOptionName);
        JsonNode hookGroup = new JsonObject
        {
            ["matcher"] = "*",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["args"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
                ["timeout"] = 30,
            }),
        };
        postToolUse.Add(hookGroup);

        try
        {
            Directory.CreateDirectory(claudeDirectory);
            await edit.PublishAsync(settingsPath, settings, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to write Claude settings at {Path}.", settingsPath);
            return TelemetryHookSkipReason.WriteFailed;
        }
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
                if (IsAspireHook(hooks[hookIndex]))
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

    private static bool IsAspireHook(JsonNode? node)
    {
        // Match legacy scripts or our exact CLI invocation, not arbitrary Aspire commands.
        if (node is not JsonObject hook)
        {
            return false;
        }
        if (hook["command"] is JsonValue commandValue
            && commandValue.TryGetValue<string>(out var command)
            && ReferencesTelemetryScript(command))
        {
            return true;
        }
        if (hook["args"] is JsonArray args)
        {
            var values = args.Select(arg => arg is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).ToArray();
            var executable = hook["command"] is JsonValue executableValue && executableValue.TryGetValue<string>(out var executableText)
                ? executableText : null;
            var (currentCommand, _) = AgentTelemetryHook.GetCommand(AgentTelemetryProtocol.HookOptionName);
            var executableName = Path.GetFileNameWithoutExtension(executable);
            var isDotnet = string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase);
            var isDirectAspireInvocation = !isDotnet
                && values is [AgentTelemetryProtocol.AgentCommandName, AgentTelemetryProtocol.TelemetryCommandName, AgentTelemetryProtocol.HookOptionName]
                && (executable == currentCommand || string.Equals(executableName, "aspire", StringComparison.OrdinalIgnoreCase));
            var isManagedAspireInvocation = isDotnet
                && values is [var assemblyPath, AgentTelemetryProtocol.AgentCommandName, AgentTelemetryProtocol.TelemetryCommandName, AgentTelemetryProtocol.HookOptionName]
                && string.Equals(Path.GetFileName(assemblyPath), "aspire.dll", StringComparison.OrdinalIgnoreCase);

            return isDirectAspireInvocation || isManagedAspireInvocation || values.Any(ReferencesTelemetryScript);
        }

        return false;
    }

    private static bool ReferencesTelemetryScript(string? value)
        => value is not null
            && (value.Contains(TelemetryHookInstaller.ShellResourceName, StringComparison.OrdinalIgnoreCase)
                || value.Contains(TelemetryHookInstaller.PowerShellResourceName, StringComparison.OrdinalIgnoreCase));

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
