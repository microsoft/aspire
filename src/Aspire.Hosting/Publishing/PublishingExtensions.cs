// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Extension methods for <see cref="IReportingStep"/> and <see cref="IReportingTask"/> to provide direct operations.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExportIgnore(Reason = "Convenience wrapper over pipeline APIs — use the dedicated ATS pipeline exports instead.")]
public static class PublishingExtensions
{
    /// <summary>
    /// Completes a publishing step successfully.
    /// </summary>
    /// <param name="step">The step to complete.</param>
    /// <param name="message">Optional completion message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed step.</returns>
    public static async Task<IReportingStep> SucceedAsync(
        this IReportingStep step,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var completionText = message ?? "Completed successfully";
        await step.CompleteAsync(completionText, CompletionState.Completed, cancellationToken).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Completes a publishing step successfully with a Markdown-formatted message.
    /// </summary>
    /// <param name="step">The step to complete.</param>
    /// <param name="message">The Markdown-formatted completion message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed step.</returns>
    public static async Task<IReportingStep> SucceedAsync(
        this IReportingStep step,
        MarkdownString message,
        CancellationToken cancellationToken = default)
    {
        await step.CompleteAsync(message, CompletionState.Completed, cancellationToken).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Completes a publishing step with a warning.
    /// </summary>
    /// <param name="step">The step to complete.</param>
    /// <param name="message">Optional completion message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed step.</returns>
    public static async Task<IReportingStep> WarnAsync(
        this IReportingStep step,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var completionText = message ?? "Completed with warnings";
        await step.CompleteAsync(completionText, CompletionState.CompletedWithWarning, cancellationToken).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Completes a publishing step with a warning and Markdown-formatted message.
    /// </summary>
    /// <param name="step">The step to complete.</param>
    /// <param name="message">The Markdown-formatted warning message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed step.</returns>
    public static async Task<IReportingStep> WarnAsync(
        this IReportingStep step,
        MarkdownString message,
        CancellationToken cancellationToken = default)
    {
        await step.CompleteAsync(message, CompletionState.CompletedWithWarning, cancellationToken).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Completes a publishing step with an error.
    /// </summary>
    /// <param name="step">The step to complete.</param>
    /// <param name="errorMessage">Optional error message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed step.</returns>
    public static async Task<IReportingStep> FailAsync(
        this IReportingStep step,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var completionText = errorMessage ?? "Failed";
        await step.CompleteAsync(completionText, CompletionState.CompletedWithError, cancellationToken).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Completes a publishing step with an error and Markdown-formatted message.
    /// </summary>
    /// <param name="step">The step to complete.</param>
    /// <param name="errorMessage">The Markdown-formatted error message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed step.</returns>
    public static async Task<IReportingStep> FailAsync(
        this IReportingStep step,
        MarkdownString errorMessage,
        CancellationToken cancellationToken = default)
    {
        await step.CompleteAsync(errorMessage, CompletionState.CompletedWithError, cancellationToken).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Updates the status text of a publishing task.
    /// </summary>
    /// <param name="task">The task to update.</param>
    /// <param name="statusText">The new status text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated task.</returns>
    public static async Task<IReportingTask> UpdateStatusAsync(
        this IReportingTask task,
        string statusText,
        CancellationToken cancellationToken = default)
    {
        await task.UpdateAsync(statusText, cancellationToken).ConfigureAwait(false);
        return task;
    }

    /// <summary>
    /// Updates the status text of a publishing task with Markdown-formatted text.
    /// </summary>
    /// <param name="task">The task to update.</param>
    /// <param name="statusText">The new Markdown-formatted status text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated task.</returns>
    public static async Task<IReportingTask> UpdateStatusAsync(
        this IReportingTask task,
        MarkdownString statusText,
        CancellationToken cancellationToken = default)
    {
        await task.UpdateAsync(statusText, cancellationToken).ConfigureAwait(false);
        return task;
    }

    /// <summary>
    /// Completes a publishing task successfully.
    /// </summary>
    /// <param name="task">The task to complete.</param>
    /// <param name="message">Optional completion message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed task.</returns>
    public static async Task<IReportingTask> SucceedAsync(
        this IReportingTask task,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        await task.CompleteAsync(message, CompletionState.Completed, cancellationToken).ConfigureAwait(false);
        return task;
    }

    /// <summary>
    /// Completes a publishing task successfully with a Markdown-formatted message.
    /// </summary>
    /// <param name="task">The task to complete.</param>
    /// <param name="message">The Markdown-formatted completion message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed task.</returns>
    public static async Task<IReportingTask> SucceedAsync(
        this IReportingTask task,
        MarkdownString message,
        CancellationToken cancellationToken = default)
    {
        await task.CompleteAsync(message, CompletionState.Completed, cancellationToken).ConfigureAwait(false);
        return task;
    }

    /// <summary>
    /// Completes a publishing task with a warning.
    /// </summary>
    /// <param name="task">The task to complete.</param>
    /// <param name="message">Optional completion message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed task.</returns>
    public static async Task<IReportingTask> WarnAsync(
        this IReportingTask task,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        await task.CompleteAsync(message, CompletionState.CompletedWithWarning, cancellationToken).ConfigureAwait(false);
        return task;
    }

    /// <summary>
    /// Completes a publishing task with a warning and Markdown-formatted message.
    /// </summary>
    /// <param name="task">The task to complete.</param>
    /// <param name="message">The Markdown-formatted warning message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed task.</returns>
    public static async Task<IReportingTask> WarnAsync(
        this IReportingTask task,
        MarkdownString message,
        CancellationToken cancellationToken = default)
    {
        await task.CompleteAsync(message, CompletionState.CompletedWithWarning, cancellationToken).ConfigureAwait(false);
        return task;
    }

    /// <summary>
    /// Completes a publishing task with an error.
    /// </summary>
    /// <param name="task">The task to complete.</param>
    /// <param name="errorMessage">Optional error message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed task.</returns>
    public static async Task<IReportingTask> FailAsync(
        this IReportingTask task,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        await task.CompleteAsync(errorMessage, CompletionState.CompletedWithError, cancellationToken).ConfigureAwait(false);
        return task;
    }

    /// <summary>
    /// Completes a publishing task with an error and Markdown-formatted message.
    /// </summary>
    /// <param name="task">The task to complete.</param>
    /// <param name="errorMessage">The Markdown-formatted error message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed task.</returns>
    public static async Task<IReportingTask> FailAsync(
        this IReportingTask task,
        MarkdownString errorMessage,
        CancellationToken cancellationToken = default)
    {
        await task.CompleteAsync(errorMessage, CompletionState.CompletedWithError, cancellationToken).ConfigureAwait(false);
        return task;
    }
}
