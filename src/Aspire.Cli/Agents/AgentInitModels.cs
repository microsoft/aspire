// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Identifies a configuration or payload managed during agent setup.
/// </summary>
internal enum AgentAssetKind
{
    Mcp,
    Playwright,
    DotnetInspect,
    AspireSkills,
    TelemetryHooks
}

/// <summary>
/// The independent asset choices made before selecting configuration environments.
/// </summary>
internal sealed record AgentAssetSelection(bool Mcp, bool Playwright, bool DotnetInspect, bool AspireSkills)
{
    public bool HasAssets => Mcp || Playwright || DotnetInspect || AspireSkills;
}

/// <summary>
/// Selected configuration environments and independently detected clients.
/// </summary>
internal sealed record AgentInitRequest(
    DirectoryInfo WorkspaceRoot,
    AgentAssetSelection Assets,
    IReadOnlyList<IAgentEnvironmentScanner> Environments,
    IReadOnlyList<AgentClientDetection> Detections);

internal enum AgentConfigurationScope
{
    Project,
    User
}

internal enum AgentConfigurationStatus
{
    Configured,
    Unchanged,
    Skipped,
    Blocked,
    Failed
}

/// <summary>
/// Describes the outcome at one native configuration or skill destination.
/// </summary>
internal sealed record AgentTargetResult(
    AgentAssetKind Asset,
    IReadOnlyList<IAgentEnvironmentScanner> Environments,
    string TargetPath,
    AgentConfigurationScope Scope,
    AgentConfigurationStatus Status,
    string? Message);

/// <summary>
/// Aggregates configuration outcomes without claiming client-owned content was acquired.
/// </summary>
internal sealed record AgentInitResult(IReadOnlyList<AgentTargetResult> Targets)
{
    public bool HasErrors => Targets.Any(static target =>
        target.Asset is not AgentAssetKind.TelemetryHooks &&
        target.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed);

    public bool HasWarnings => Targets.Any(static target =>
        target.Status is AgentConfigurationStatus.Skipped or AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed);

    public IReadOnlyList<IAgentEnvironmentScanner> RegisteredEnvironments => Targets
        .Where(static target => target.Asset is AgentAssetKind.AspireSkills &&
            target.Status is AgentConfigurationStatus.Configured or AgentConfigurationStatus.Unchanged)
        .SelectMany(static target => target.Environments)
        .Distinct()
        .ToArray();
}
