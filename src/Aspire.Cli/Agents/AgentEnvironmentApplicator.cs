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
    public AgentEnvironmentApplicator(
        string description, 
        Func<CancellationToken, Task> applyCallback,
        McpInitPromptGroup? promptGroup = null,
        int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(applyCallback);

        Description = description;
        _applyCallback = applyCallback;
        PromptGroup = promptGroup ?? McpInitPromptGroup.AgentEnvironments;
        Priority = priority;
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
    /// Applies the configuration changes to enable the agent environment.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await _applyCallback(cancellationToken);
    }
}
