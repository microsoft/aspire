// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Copilot;

internal sealed class CopilotAgentClient(
    CliExecutionContext context, IEnvironment environment, ILogger<CopilotAgentClient> logger)
    : IAgentClient
{
    public const string ClientId = "copilot";

    private static readonly McpConfigFileHelper.Configuration s_mcp = new()
    {
        ServersPropertyName = "mcpServers",
        JsonDocumentOptions = ConfigurationHelper.ParseOptions,
        GlobalServerConfiguration = new JsonObject
        {
            ["type"] = "local",
            ["command"] = "aspire",
            ["args"] = new JsonArray("agent", "mcp"),
            ["env"] = new JsonObject { ["DOTNET_ROOT"] = "${DOTNET_ROOT}" },
            ["tools"] = new JsonArray("*")
        }
    };

    public string Id => ClientId;
    public string DisplayName { get; } = AgentCommandStrings.Environment_Copilot;
    public AgentEnvironment Environments => AgentEnvironment.CopilotCli | AgentEnvironment.CopilotApp | AgentEnvironment.VsCode;

    public string GetSkillBaseDirectory(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => Path.Combine(scope is AgentConfigurationScope.Project ? workspaceRoot.FullName : context.HomeDirectory.FullName,
            AgentPath.UniversalSkillsDirectory);

    public IEnumerable<string> GetLocalSkillDirectories(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        // https://docs.github.com/en/copilot/concepts/agents/about-agent-skills
        var root = scope is AgentConfigurationScope.Project ? workspaceRoot.FullName : context.HomeDirectory.FullName;
        yield return GetSkillBaseDirectory(scope, workspaceRoot);
        yield return Path.Combine(root, ".claude", "skills");
        yield return scope is AgentConfigurationScope.Project
            ? Path.Combine(root, ".github", "skills")
            : Path.Combine(GetSettingsDirectory(), "skills");
    }

    public bool HasIntegrationRegistration(
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, CancellationToken cancellationToken)
        => AspireSkillsRemoteConfiguration.HasPluginRegistration(GetSettingsFiles(workspaceRoot, scope), cancellationToken);

    public string GetIntegrationConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        var directory = scope is AgentConfigurationScope.Project
            ? Path.Combine(workspaceRoot.FullName, ".github", "copilot") : GetSettingsDirectory();

        return Path.Combine(directory, "settings.json");
    }

    public Task UpdateIntegrationConfigAsync(
        JsonObject config, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken)
        => AspireSkillsRemoteConfiguration.ApplyPluginAsync(config, GetSettingsFiles(workspaceRoot, scope),
            GetIntegrationConfigPath(scope, workspaceRoot), edit, cancellationToken);

    public string GetMcpConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => scope is AgentConfigurationScope.Project ? Path.Combine(workspaceRoot.FullName, ".mcp.json")
            : Path.Combine(GetSettingsDirectory(), "mcp-config.json");

    public IEnumerable<(string Path, bool ConfigureServer)> GetMcpConfigPaths(
        AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        yield return (GetMcpConfigPath(scope, workspaceRoot), true);
        if (scope is AgentConfigurationScope.Project)
        {
            // Repairs share the selected client's scope and consent, but never add a server.
            foreach (var path in new[] { Path.Combine(".vscode", "mcp.json"), Path.Combine(".github", "copilot", "mcp.json") })
            {
                yield return (Path.Combine(workspaceRoot.FullName, path), false);
            }
        }
    }

    public async Task<(JsonObject Config, string Path)> ReadMcpConfigAsync(
        string configFilePath, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken)
        => (await McpConfigFileHelper.ReadConfigAsync(configFilePath, s_mcp.JsonDocumentOptions, edit, cancellationToken), configFilePath);

    public (bool Repaired, bool Added) UpdateMcpConfig(
        JsonObject config, string configFilePath, bool configureServer, AgentConfigurationScope scope)
    {
        var configuration = configureServer ? s_mcp : s_mcp with { ServersPropertyName = "servers" };
        var repaired = McpConfigFileHelper.UpdateDeprecatedCommand(config, configuration.ServersPropertyName);
        var added = configureServer && McpConfigFileHelper.ConfigureMcpServer(config, configuration, scope, configFilePath);

        return (repaired, added);
    }

    public async Task<TelemetryHookConfigurationResult> ConfigureTelemetryHooksAsync(
        AgentEnvironment detectedEnvironments, CancellationToken cancellationToken)
    {
        var targetEnvironment = detectedEnvironments & (AgentEnvironment.CopilotApp | AgentEnvironment.CopilotCli);
        // App and CLI share one user hook. Prefer the App display name when both are detected.
        // Canonical scripts still attribute App events to copilot-cli:
        // https://github.com/microsoft/aspire-skills/issues/71.
        if (targetEnvironment.HasFlag(AgentEnvironment.CopilotApp))
        {
            targetEnvironment = AgentEnvironment.CopilotApp;
        }
        if (targetEnvironment == AgentEnvironment.None)
        {
            return new TelemetryHookConfigurationResult(AgentEnvironment.None, []);
        }

        try
        {
            // Copilot reads Claude settings only at project scope, so these user hooks cannot
            // duplicate Claude's user hook. Direct exec also avoids a shell launch per tool call.
            // https://docs.github.com/en/copilot/reference/hooks-reference#command-hooks
            var hooksDirectory = Path.Combine(GetSettingsDirectory(), "hooks");
            Directory.CreateDirectory(hooksDirectory);
            var filePath = Path.Combine(hooksDirectory, "aspire-telemetry.json");
            var (command, args) = AgentTelemetryHook.GetCommand(AgentTelemetryProtocol.HookOptionName);
            var config = new JsonObject
            {
                ["version"] = 1,
                ["hooks"] = new JsonObject
                {
                    ["postToolUse"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["exec"] = command,
                            ["args"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
                            ["timeoutSec"] = 30,
                        }),
                },
            };
            var edit = new AgentConfiguration();
            await edit.ReadBytesForEditAsync(filePath, cancellationToken);
            await edit.PublishAsync(filePath, config, cancellationToken);

            return new TelemetryHookConfigurationResult(targetEnvironment, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to write Copilot CLI telemetry hook configuration.");
            return new TelemetryHookConfigurationResult(AgentEnvironment.None,
                [new TelemetryHookSkip(targetEnvironment, TelemetryHookSkipReason.WriteFailed)]);
        }
    }

    public override string ToString() => Id;

    private string GetSettingsDirectory()
        => AgentPath.Expand(CopilotPaths.GetConfigDirectory(context.HomeDirectory, environment), context);

    private IEnumerable<string> GetSettingsFiles(DirectoryInfo workspaceRoot, AgentConfigurationScope scope)
    {
        yield return Path.Combine(GetSettingsDirectory(), "settings.json");
        // Copilot consumes Claude project settings, but not Claude user settings.
        // https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference
        if (scope is AgentConfigurationScope.Project)
        {
            yield return Path.Combine(workspaceRoot.FullName, ".claude", "settings.json");
            yield return Path.Combine(workspaceRoot.FullName, ".claude", "settings.local.json");
            yield return Path.Combine(workspaceRoot.FullName, ".github", "copilot", "settings.json");
            yield return Path.Combine(workspaceRoot.FullName, ".github", "copilot", "settings.local.json");
        }
        yield return Path.Combine(AgentPath.GetManagedSettingsDirectory(context, environment, "GitHubCopilot", "github-copilot"),
            "managed-settings.json");
    }
}
