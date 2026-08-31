// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents an agent environment that was detected and can be configured.
/// </summary>
internal sealed class AgentEnvironmentApplicator
{
    private readonly Func<CancellationToken, Task> _applyCallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentEnvironmentApplicator"/> class.
    /// </summary>
    /// <param name="description">The description shown in selection prompts.</param>
    /// <param name="applyCallback">The callback to apply the configuration.</param>
    /// <param name="promptGroup">The prompt group this applicator belongs to. Defaults to AgentEnvironments.</param>
    /// <param name="priority">The priority within the prompt group (lower numbers first). Defaults to 0.</param>
    /// <param name="assetKind">The agent asset kind that owns this action, if any.</param>
    /// <param name="targetId">The stable identifier used to de-duplicate equivalent action targets.</param>
    public AgentEnvironmentApplicator(
        string description,
        Func<CancellationToken, Task> applyCallback,
        McpInitPromptGroup? promptGroup = null,
        int priority = 0,
        AgentAssetKind? assetKind = null,
        string? targetId = null)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(applyCallback);
        if (assetKind is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        }

        Description = description;
        _applyCallback = applyCallback;
        PromptGroup = promptGroup ?? McpInitPromptGroup.AgentEnvironments;
        Priority = priority;
        AssetKind = assetKind;
        TargetId = targetId;
    }

    /// <summary>
    /// Gets the description of the agent environment shown in the selection prompt.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the prompt group this applicator belongs to.
    /// </summary>
    public McpInitPromptGroup PromptGroup { get; }

    /// <summary>
    /// Gets the priority within the prompt group for ordering (lower numbers first).
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Gets the agent asset kind that owns this action, if any.
    /// </summary>
    public AgentAssetKind? AssetKind { get; }

    /// <summary>
    /// Gets the stable identifier for the action target, if any.
    /// </summary>
    public string? TargetId { get; }

    /// <summary>
    /// Applies the configuration changes to enable the agent environment.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await _applyCallback(cancellationToken);
    }
}
