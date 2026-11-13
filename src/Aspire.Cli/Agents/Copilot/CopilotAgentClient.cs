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

    public IEnumerable<string> GetForwardedMcpConfigPaths(AgentConfigurationScope scope)
    {
        if (scope is not AgentConfigurationScope.Global)
        {
            yield break;
        }

        // VS Code forwards user-profile MCP servers to the Copilot harness. The default profile
        // stores them in User/mcp.json and named profiles in User/profiles/<id>/mcp.json:
        // https://code.visualstudio.com/docs/agents/reference/mcp-configuration
        var home = context.HomeDirectory.FullName;
        var userDataRoot = environment.IsWindows()
            ? AgentPath.GetOverride("APPDATA", context, environment) ?? Path.Combine(home, "AppData", "Roaming")
            : environment.IsMacOS()
                ? Path.Combine(home, "Library", "Application Support")
                : AgentPath.GetOverride("XDG_CONFIG_HOME", context, environment) ?? Path.Combine(home, ".config");
        foreach (var product in new[] { "Code", "Code - Insiders" })
        {
            var userDirectory = Path.Combine(userDataRoot, product, "User");
            yield return Path.Combine(userDirectory, "mcp.json");

            var profilesDirectory = new DirectoryInfo(Path.Combine(userDirectory, "profiles"));
            if (profilesDirectory.Exists)
            {
                foreach (var profile in profilesDirectory.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }))
                {
                    yield return Path.Combine(profile.FullName, "mcp.json");
                }
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
    // ~/.copilot/hooks, so one registration covers all of them:
    // https://code.visualstudio.com/docs/agent-customization/hooks#_choose-a-hook-source
    public bool SupportsTelemetryHooks => true;

    public async Task<TelemetryHookStatus> ConfigureTelemetryHookAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Copilot reads Claude settings only at project scope, so these user hooks cannot
            // duplicate Claude's user hook. Direct exec avoids a shell launch, and the matcher keeps
            // Copilot from launching the CLI for tool calls the classifier would discard.
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
