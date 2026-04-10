// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a publishing step, which can contain multiple tasks.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
internal sealed class ReportingStep : IReportingStep
{
    private readonly ConcurrentDictionary<string, ReportingTask> _tasks = new();

    internal ReportingStep(PipelineActivityReporter reporter, string id, string title, string? parentStepId = null, int hierarchyLevel = 0)
    {
        Reporter = reporter;
        Id = id;
        Title = title;
        ParentStepId = parentStepId;
        HierarchyLevel = hierarchyLevel;
    }

    /// <summary>
    /// Unique Id of the step.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The title of the publishing step.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// The identifier of the parent step used for hierarchical display.
    /// </summary>
    internal string? ParentStepId { get; }

    /// <summary>
    /// The hierarchical level of the step used for display.
    /// </summary>
    internal int HierarchyLevel { get; }

    /// <summary>
    /// The completion state of the step. Defaults to InProgress.
    /// The state is only aggregated from child tasks during disposal.
    /// </summary>
    public CompletionState CompletionState
    {
        get => _completionState;
        internal set => _completionState = value;
    }

    private CompletionState _completionState = CompletionState.InProgress;

    /// <summary>
    /// The completion text for the step.
    /// </summary>
    public string CompletionText { get; internal set; } = string.Empty;

    /// <summary>
    /// The collection of child tasks belonging to this step.
    /// </summary>
    public IReadOnlyDictionary<string, ReportingTask> Tasks => _tasks;

    /// <summary>
    /// The progress reporter that created this step.
    /// </summary>
    internal PipelineActivityReporter Reporter { get; }

    /// <summary>
    /// Adds a task to this step.
    /// </summary>
    internal void AddTask(ReportingTask task)
    {
        _tasks.TryAdd(task.Id, task);
    }

    /// <summary>
    /// Recalculates the completion state based on child tasks.
    /// </summary>
    internal CompletionState CalculateAggregatedState()
    {
        if (_tasks.IsEmpty)
        {
            return CompletionState.Completed;
        }

        var maxState = CompletionState.InProgress;
        foreach (var task in _tasks.Values)
        {
            if ((int)task.CompletionState > (int)maxState)
            {
                maxState = task.CompletionState;
            }
        }
        return maxState;
    }

    /// <inheritdoc />
    public async Task<IReportingTask> CreateTaskAsync(string statusText, CancellationToken cancellationToken = default)
    {
        if (Reporter is null)
        {
            throw new InvalidOperationException("Cannot create task: Reporter is not set.");
        }

        return await Reporter.CreateTaskAsync(this, statusText, enableMarkdown: false, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReportingTask> CreateTaskAsync(MarkdownString statusText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusText);

        if (Reporter is null)
        {
            throw new InvalidOperationException("Cannot create task: Reporter is not set.");
        }

        return await Reporter.CreateTaskAsync(this, statusText.Value, enableMarkdown: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
#pragma warning disable CS0618 // Type or member is obsolete
    public void Log(LogLevel logLevel, string message, bool enableMarkdown = true)
#pragma warning restore CS0618
    {
        if (Reporter is null)
        {
            return;
        }

        Reporter.Log(this, logLevel, message, enableMarkdown);
    }

    /// <inheritdoc />
    public void Log(LogLevel logLevel, string message)
    {
        if (Reporter is null)
        {
            return;
        }

        Reporter.Log(this, logLevel, message, enableMarkdown: false);
    }

    /// <inheritdoc />
    public void Log(LogLevel logLevel, MarkdownString message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Reporter is null)
        {
            return;
        }

        Reporter.Log(this, logLevel, message.Value, enableMarkdown: true);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string completionText, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
    {
        if (Reporter is null)
        {
            throw new InvalidOperationException("Cannot complete step: Reporter is not set.");
        }

        await Reporter.CompleteStepAsync(this, completionText, completionState, enableMarkdown: false, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(MarkdownString completionText, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completionText);

        if (Reporter is null)
        {
            throw new InvalidOperationException("Cannot complete step: Reporter is not set.");
        }

        await Reporter.CompleteStepAsync(this, completionText.Value, completionState, enableMarkdown: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the step and aggregates the final completion state from all child tasks.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Reporter is null)
        {
            return;
        }

        // Only complete the step if it's still in progress to avoid double completion
        if (CompletionState != CompletionState.InProgress)
        {
            return;
        }

        // Use the current completion state or calculate it from child tasks if still in progress
        var finalState = CalculateAggregatedState();

        // Only set completion text if it has not been explicitly set
        var completionText = string.IsNullOrEmpty(CompletionText)
            ? finalState switch
            {
                CompletionState.Completed => $"{Title} completed successfully",
                CompletionState.CompletedWithWarning => $"{Title} completed with warnings",
                CompletionState.CompletedWithError => $"{Title} completed with errors",
                _ => $"{Title} completed"
            }
            : CompletionText;

        await Reporter.CompleteStepAsync(this, completionText, finalState, enableMarkdown: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }
}
