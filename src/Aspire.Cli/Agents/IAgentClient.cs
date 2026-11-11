// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Agents;

/// <summary>
/// A supported client's identity and configuration behavior.
/// </summary>
internal interface IAgentClient
{
    string Id { get; }
    string DisplayName { get; }
    AgentEnvironment Environments { get; }

    string GetSkillBaseDirectory(AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    // Include compatibility locations the client loads, not just the CLI's install destination.
    IEnumerable<string> GetLocalSkillDirectories(AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    bool HasIntegrationRegistration(
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, IReadOnlyList<SkillSource> sources);

    string GetIntegrationConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    Task UpdateIntegrationConfigAsync(
        JsonObject config, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, IReadOnlyList<SkillSource> sources, CancellationToken cancellationToken);

    string GetMcpConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    IEnumerable<(string Path, bool ConfigureServer)> GetMcpConfigPaths(
        AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    Task<(JsonObject Config, string Path)> ReadMcpConfigAsync(
        string configFilePath, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken);

    (bool Repaired, bool Added) UpdateMcpConfig(
        JsonObject config, string configFilePath, bool configureServer, AgentConfigurationScope scope);

    // Telemetry hooks are registered once per client, so a selected client covers all of its environments.
    // False means the client has no verified hook schema, and the telemetry hook members throw NotSupportedException.
    bool SupportsTelemetryHooks { get; }

    Task<TelemetryHookStatus> ConfigureTelemetryHookAsync(
        AgentConfigurationScope scope, DirectoryInfo workspaceRoot, CancellationToken cancellationToken);

    // Called on every recorded tool call by a hook this client installed at the given scope. Agents run every
    // configured hook without deduplicating them, so return false when another Aspire hook records the same call.
    // Return true when that cannot be determined so a call is never left unrecorded.
    bool ShouldRecordTelemetryHook(AgentConfigurationScope scope);
}

internal enum AgentConfigurationScope
{
    Project,
    Global
}
