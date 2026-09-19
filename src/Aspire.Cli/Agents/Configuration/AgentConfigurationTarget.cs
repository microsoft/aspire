// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Supplies native targets for selected clients and assets without acquiring content.
/// </summary>
internal interface IAgentConfigurationHandler
{
    IEnumerable<AgentConfigurationTarget> GetTargets(AgentInitRequest request);
}

/// <summary>
/// A mutation of one named entry in a native settings file.
/// </summary>
internal sealed record AgentConfigurationTarget(
    string Path,
    AgentConfigurationScope Scope,
    AgentAssetKind Asset,
    IReadOnlyList<AgentClientKind> Clients,
    string Entry,
    Func<JsonObject, AgentConfigurationMutationContext, CancellationToken, Task<AgentConfigurationEdit>> ApplyAsync);

/// <summary>
/// The outcome of an in-memory mutation. The writer determines whether a successful edit changed anything.
/// </summary>
internal sealed record AgentConfigurationEdit(AgentConfigurationStatus Status, string Message)
{
    public static AgentConfigurationEdit Applied(string message) => new(AgentConfigurationStatus.Configured, message);
    public static AgentConfigurationEdit Skipped(string message) => new(AgentConfigurationStatus.Skipped, message);
    public static AgentConfigurationEdit Blocked(string message) => new(AgentConfigurationStatus.Blocked, message);
}

/// <summary>
/// Fresh configuration reads and successful outcomes available to a grouped mutation.
/// </summary>
internal sealed class AgentConfigurationMutationContext(
    AgentConfigurationReadContext reader,
    IReadOnlyList<AgentTargetResult> completed,
    IReadOnlyList<AgentTargetResult> pending)
{
    public Task<JsonObject?> ReadOptionalAsync(string path, CancellationToken cancellationToken)
        => reader.ReadOptionalAsync(path, cancellationToken);

    public bool HasAspireConfiguration(AgentClientKind client)
        => completed.Concat(pending).Any(result =>
            result.Clients.Contains(client) &&
            result.Asset is AgentAssetKind.AspireSkills or AgentAssetKind.Mcp &&
            result.Status is AgentConfigurationStatus.Configured or AgentConfigurationStatus.Unchanged);
}

/// <summary>
/// A configuration conflict that must leave the affected entry untouched.
/// </summary>
internal sealed class AgentConfigurationException(string message) : Exception(message);
