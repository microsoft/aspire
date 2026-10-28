// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
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
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, CancellationToken cancellationToken);

    string GetIntegrationConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    Task UpdateIntegrationConfigAsync(
        JsonObject config, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken);

    string GetMcpConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    IEnumerable<(string Path, bool ConfigureServer)> GetMcpConfigPaths(
        AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    Task<(JsonObject Config, string Path)> ReadMcpConfigAsync(
        string configFilePath, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        AgentConfiguration edit, CancellationToken cancellationToken);

    (bool Repaired, bool Added) UpdateMcpConfig(
        JsonObject config, string configFilePath, bool configureServer, AgentConfigurationScope scope);

    Task<TelemetryHookConfigurationResult> ConfigureTelemetryHooksAsync(
        AgentEnvironment detectedEnvironments, CancellationToken cancellationToken);
}

internal enum AgentConfigurationScope
{
    Project,
    Global
}
