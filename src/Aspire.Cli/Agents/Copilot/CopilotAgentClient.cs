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

    /// <summary>
    /// The hook file name used at both user scope (<c>~/.copilot/hooks</c>) and project scope (<c>.github/hooks</c>).
    /// </summary>
    internal const string TelemetryHookFileName = "aspire-telemetry.json";

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

    /// <summary>
    /// User-level <c>postToolUse</c> <c>matcher</c> for the skill and Aspire MCP tools.
    /// </summary>
    /// <remarks>
    /// Copilot matchers see only the tool name, so matching file-read tools would launch the CLI for every
    /// read in every repository. Copilot anchors the pattern itself, and the explicit anchors keep it correct
    /// when evaluated as a plain regular expression (https://docs.github.com/en/copilot/reference/hooks-reference).
    /// </remarks>
    internal static string TelemetryHookMatcher { get; } = $"^(?:[Ss]kill|{AgentTelemetryHook.McpMatcher})$";

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
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, IReadOnlyList<SkillSource> sources)
        => AspireSkillsRemoteConfiguration.HasPluginRegistration(GetSettingsFiles(workspaceRoot, scope), sources);

    public string GetIntegrationConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        var directory = scope is AgentConfigurationScope.Project
            ? Path.Combine(workspaceRoot.FullName, ".github", "copilot") : GetSettingsDirectory();

        return Path.Combine(directory, "settings.json");
    }

    public Task UpdateIntegrationConfigAsync(
        JsonObject config, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, IReadOnlyList<SkillSource> sources, CancellationToken cancellationToken)
        => AspireSkillsRemoteConfiguration.ApplyPluginAsync(config, GetSettingsFiles(workspaceRoot, scope),
            GetIntegrationConfigPath(scope, workspaceRoot), edit, sources, cancellationToken);

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

    // VS Code, the Copilot App, and the Copilot CLI share the Copilot harness, which reads user hooks from
    // ~/.copilot/hooks and repository hooks from .github/hooks, so one registration covers all of them:
    // https://code.visualstudio.com/docs/agent-customization/hooks#_choose-a-hook-source
    public bool SupportsTelemetryHooks => true;

    public async Task<TelemetryHookStatus> ConfigureTelemetryHookAsync(
        AgentConfigurationScope scope, DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        try
        {
            // Project hooks live in .github/hooks so they can be committed. Copilot also runs Claude project
            // hooks, so TelemetryHookSkipPolicy drops those inside Copilot. Direct exec avoids a shell launch
            // and shell quoting, and the matcher keeps Copilot from launching the CLI for tool calls the
            // classifier would discard.
            // Copilot cloud agent honors only bash or command entries, so it ignores this hook; its sandbox
            // has no Aspire CLI at this path and a restricted network.
            // https://docs.github.com/en/copilot/reference/hooks-reference#hooks-locations
            // https://docs.github.com/en/copilot/reference/hooks-reference#cloud-agent-execution-environment
            var hooksDirectory = scope is AgentConfigurationScope.Project
                ? GetProjectHooksDirectory(workspaceRoot.FullName)
                : Path.Combine(GetSettingsDirectory(), "hooks");
            Directory.CreateDirectory(hooksDirectory);
            var filePath = Path.Combine(hooksDirectory, TelemetryHookFileName);
            var (command, args) = AgentTelemetryHook.GetHookCommand(Id, scope);
            var config = new JsonObject
            {
                ["version"] = 1,
                ["hooks"] = new JsonObject
                {
                    ["postToolUse"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["matcher"] = TelemetryHookMatcher,
                            ["exec"] = command,
                            ["args"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
                            ["timeoutSec"] = 30,
                        }),
                },
            };
            var edit = new AgentConfiguration();
            await edit.ReadBytesForEditAsync(filePath, cancellationToken);
            await edit.PublishAsync(filePath, config, cancellationToken);

            return TelemetryHookStatus.Configured;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to write Copilot telemetry hook configuration.");
            return TelemetryHookStatus.WriteFailed;
        }
    }

    /// <summary>
    /// Determines whether the repository installs Aspire's Copilot telemetry hook at project scope.
    /// </summary>
    /// <remarks>
    /// A user-scope hook uses this to defer to the project hook so one tool call is recorded once. Callers handle
    /// I/O and JSON failures. Copilot lists handlers directly under the event:
    /// <c>{"hooks":{"postToolUse":[{"exec":"/path/to/aspire","args":[...,"--scope","project"]}]}}</c>
    /// </remarks>
    public bool HasProjectTelemetryHook(DirectoryInfo repositoryRoot)
    {
        var path = Path.Combine(GetProjectHooksDirectory(repositoryRoot.FullName), TelemetryHookFileName);

        return File.Exists(path)
            && JsonNode.Parse(File.ReadAllText(path)) is JsonObject config
            && config["hooks"] is JsonObject hooks
            && hooks["postToolUse"] is JsonArray postToolUse
            && postToolUse.OfType<JsonObject>().Any(hook => AgentTelemetryHook.IsProjectHookInvocation(hook["exec"], hook["args"]));
    }

    public override string ToString() => Id;

    private static string GetProjectHooksDirectory(string workspaceRoot) => Path.Combine(workspaceRoot, ".github", "hooks");

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
