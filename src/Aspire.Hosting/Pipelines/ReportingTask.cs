// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a publishing task, which belongs to a step.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
internal sealed class ReportingTask : IReportingTask
{
    internal ReportingTask(string id, string stepId, string statusText, ReportingStep parentStep)
    {
        Id = id;
        StepId = stepId;
        StatusText = statusText;
        ParentStep = parentStep;
    }

    /// <summary>
    /// Unique Id of the task.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The identifier of the step this task belongs to.
    /// </summary>
    public string StepId { get; }

    /// <summary>
    /// Reference to the parent step this task belongs to.
    /// </summary>
    public ReportingStep ParentStep { get; }

    /// <summary>
    /// The current status text of the task.
    /// </summary>
    public string StatusText { get; internal set; }

    /// <summary>
    /// The completion state of the task.
    /// </summary>
    public CompletionState CompletionState { get; internal set; } = CompletionState.InProgress;

    /// <summary>
    /// Optional completion message for the task.
    /// </summary>
    public string CompletionMessage { get; internal set; } = string.Empty;

    /// <inheritdoc />
    public async Task UpdateAsync(string statusText, CancellationToken cancellationToken = default)
    {
        await ParentStep.Reporter.UpdateTaskAsync(this, statusText, enableMarkdown: false, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(MarkdownString statusText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusText);
        await ParentStep.Reporter.UpdateTaskAsync(this, statusText.Value, enableMarkdown: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string? completionMessage = null, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
    {
        await ParentStep.Reporter.CompleteTaskAsync(this, completionState, completionMessage, enableMarkdown: false, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(MarkdownString completionMessage, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completionMessage);
        await ParentStep.Reporter.CompleteTaskAsync(this, completionState, completionMessage.Value, enableMarkdown: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the task, completing it successfully if not already completed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (CompletionState != CompletionState.InProgress)
        {
            return;
        }

        // Auto-complete with success if not already completed
        await CompleteAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }
}
