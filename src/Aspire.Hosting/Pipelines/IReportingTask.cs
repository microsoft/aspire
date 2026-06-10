// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a publishing task, which belongs to a step.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IReportingTask : IAsyncDisposable
{
    /// <summary>
    /// Updates the status text of this task.
    /// </summary>
    /// <param name="statusText">The new status text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task UpdateAsync(string statusText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the status text of this task with Markdown-formatted text.
    /// </summary>
    /// <param name="statusText">The new Markdown-formatted status text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task UpdateAsync(MarkdownString statusText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the task with the specified completion message.
    /// </summary>
    /// <param name="completionMessage">Optional completion message that will appear as a dimmed child message.</param>
    /// <param name="completionState">The completion state of the task.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task CompleteAsync(string? completionMessage = null, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the task with a Markdown-formatted completion message.
    /// </summary>
    /// <param name="completionMessage">The Markdown-formatted completion message that will appear as a dimmed child message.</param>
    /// <param name="completionState">The completion state of the task.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task CompleteAsync(MarkdownString completionMessage, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default);
}
