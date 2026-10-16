// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

    Task<bool> HasNativeSkillsRegistrationAsync(DirectoryInfo workspaceRoot, CancellationToken cancellationToken);

    AgentFileEdit CreateNativeSkillsEdit(DirectoryInfo workspaceRoot, AgentConfigurationScope scope);

    string GetMcpConfigPath(AgentConfigurationScope scope, DirectoryInfo workspaceRoot);

    Task<IReadOnlyList<AgentFileEdit>> CreateMcpEditsAsync(
        DirectoryInfo workspaceRoot, AgentConfigurationScope scope, CancellationToken cancellationToken);
}

internal enum AgentConfigurationScope
{
    Project,
    Global
}
