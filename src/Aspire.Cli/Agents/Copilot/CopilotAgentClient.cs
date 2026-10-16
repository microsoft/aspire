// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Agents.Copilot;

internal sealed class CopilotAgentClient(CliExecutionContext context, IEnvironment environment)
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

    public Task<bool> HasNativeSkillsRegistrationAsync(DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
        => AspireSkillsConfiguration.HasPluginRegistrationAsync(GetSettingsFiles(workspaceRoot), cancellationToken);

    public AgentFileEdit CreateNativeSkillsEdit(DirectoryInfo workspaceRoot, AgentConfigurationScope scope)
    {
        var directory = scope is AgentConfigurationScope.Project
            ? Path.Combine(workspaceRoot.FullName, ".github", "copilot") : GetSettingsDirectory();

        return AspireSkillsConfiguration.CreatePluginEdit(
            this, Path.Combine(directory, "settings.json"), GetSettingsFiles(workspaceRoot));
    }

    public string GetMcpConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
        => scope is AgentConfigurationScope.Project ? Path.Combine(workspaceRoot.FullName, ".mcp.json")
            : Path.Combine(CopilotPaths.GetConfigDirectory(context.HomeDirectory, environment), "mcp-config.json");

    public Task<IReadOnlyList<AgentFileEdit>> CreateMcpEditsAsync(
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<AgentFileEdit> edits = [McpConfigFileHelper.CreateEdit(this, GetMcpConfigPath(scope, workspaceRoot), s_mcp, true, scope)];
        if (scope is AgentConfigurationScope.Project)
        {
            // Repairs share the selected client's scope and consent, but never add a server.
            foreach (var path in new[] { Path.Combine(".vscode", "mcp.json"), Path.Combine(".github", "copilot", "mcp.json") })
            {
                edits.Add(McpConfigFileHelper.CreateEdit(this, Path.Combine(workspaceRoot.FullName, path),
                    s_mcp with { ServersPropertyName = "servers" }, false, scope));
            }
        }

        return Task.FromResult<IReadOnlyList<AgentFileEdit>>(edits);
    }

    public override string ToString() => Id;

    private string GetSettingsDirectory()
        => AgentPath.Expand(CopilotPaths.GetConfigDirectory(context.HomeDirectory, environment), context);

    private IEnumerable<string> GetSettingsFiles(DirectoryInfo workspaceRoot)
    {
        yield return Path.Combine(GetSettingsDirectory(), "settings.json");
        // Copilot consumes Claude project settings, but not Claude user settings.
        // https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference
        yield return Path.Combine(workspaceRoot.FullName, ".claude", "settings.json");
        yield return Path.Combine(workspaceRoot.FullName, ".claude", "settings.local.json");
        yield return Path.Combine(workspaceRoot.FullName, ".github", "copilot", "settings.json");
        yield return Path.Combine(workspaceRoot.FullName, ".github", "copilot", "settings.local.json");
        yield return Path.Combine(AgentPath.GetManagedSettingsDirectory(context, environment, "GitHubCopilot", "github-copilot"),
            "managed-settings.json");
    }
}
