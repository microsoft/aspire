// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

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
/// The independent asset choices made before selecting clients.
/// </summary>
internal sealed record AgentAssetSelection(bool Mcp, bool Playwright, bool DotnetInspect, bool AspireSkills)
{
    public bool HasAssets => Mcp || Playwright || DotnetInspect || AspireSkills;
}

/// <summary>
/// Read-only evidence that a client is present.
/// </summary>
internal sealed record AgentClientDetection(AgentClient Client, string? Version, bool IsInsiders);

/// <summary>
/// Resolved inputs for configuring selected clients.
/// </summary>
internal sealed record AgentInitRequest(
    DirectoryInfo WorkspaceRoot,
    AgentAssetSelection Assets,
    IReadOnlyList<AgentClient> Clients,
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
    IReadOnlyList<AgentClient> Clients,
    string TargetPath,
    AgentConfigurationScope Scope,
    AgentConfigurationStatus Status,
    string? Message);

/// <summary>
/// An edit to one named entry in a native settings file.
/// </summary>
internal sealed record AgentConfigurationTarget(
    string Path,
    AgentConfigurationScope Scope,
    AgentAssetKind Asset,
    IReadOnlyList<AgentClient> Clients,
    string Entry,
    Func<JsonObject, AgentConfigurationWriter.ReadContext, CancellationToken, Task<AgentConfigurationEdit>> ApplyAsync)
{
    public AgentTargetResult ToResult(AgentConfigurationStatus status, string message)
        => new(Asset, Clients, Path, Scope, status, message);
}

/// <summary>
/// The outcome of an in-memory edit; the writer determines whether anything changed.
/// </summary>
internal sealed record AgentConfigurationEdit(AgentConfigurationStatus Status, string Message)
{
    public static AgentConfigurationEdit Applied(string message) => new(AgentConfigurationStatus.Configured, message);
    public static AgentConfigurationEdit Skipped(string message) => new(AgentConfigurationStatus.Skipped, message);
    public static AgentConfigurationEdit Blocked(string message) => new(AgentConfigurationStatus.Blocked, message);
}

/// <summary>
/// A configuration conflict that must leave the affected entry untouched.
/// </summary>
internal sealed class AgentConfigurationException(string message) : Exception(message);

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

    public IReadOnlyList<AgentClient> RegisteredClients => Targets
        .Where(static target => target.Asset is AgentAssetKind.AspireSkills &&
            target.Status is AgentConfigurationStatus.Configured or AgentConfigurationStatus.Unchanged)
        .SelectMany(static target => target.Clients)
        .Distinct()
        .ToArray();
}

/// <summary>
/// Configures selected assets and deduplicated native client targets.
/// </summary>
internal interface IAgentInitService
{
    Task<AgentInitResult> ConfigureAsync(AgentInitRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Installs only the CLI-managed Playwright and dotnet-inspect skill payloads.
/// </summary>
internal interface IAgentSkillInstaller
{
    Task<IReadOnlyList<AgentTargetResult>> InstallAsync(AgentInitRequest request, CancellationToken cancellationToken);
}
