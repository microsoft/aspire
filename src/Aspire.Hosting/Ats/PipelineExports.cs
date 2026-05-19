#pragma warning disable ASPIREPIPELINES001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for pipeline-specific helpers that need ATS-friendly payloads.
/// </summary>
internal static class PipelineExports
{
    /// <summary>
    /// Adds an application-level pipeline step in a TypeScript-friendly shape.
    /// </summary>
    /// <param name="pipeline">The distributed application pipeline.</param>
    /// <param name="stepName">The unique name of the pipeline step.</param>
    /// <param name="callback">The callback to execute when the step runs.</param>
    /// <param name="dependsOn">Optional step names that this step depends on.</param>
    /// <param name="requiredBy">Optional step names that require this step.</param>
    [AspireExport(Description = "Adds a pipeline step to the application")]
    public static void AddStep(
        this global::Aspire.Hosting.Pipelines.IDistributedApplicationPipeline pipeline,
        string stepName,
        Func<PipelineStepContext, Task> callback,
        string[]? dependsOn = null,
        string[]? requiredBy = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrEmpty(stepName);
        ArgumentNullException.ThrowIfNull(callback);

        pipeline.AddStep(stepName, callback, dependsOn, requiredBy);
    }

    /// <summary>
    /// Registers a pipeline configuration callback in a TypeScript-friendly shape.
    /// </summary>
    /// <param name="pipeline">The distributed application pipeline.</param>
    /// <param name="callback">The callback to execute during pipeline configuration.</param>
    [AspireExport(Description = "Configures the application pipeline via a callback")]
    public static void Configure(
        this global::Aspire.Hosting.Pipelines.IDistributedApplicationPipeline pipeline,
        Func<PipelineConfigurationContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(callback);

        pipeline.AddPipelineConfiguration(callback);
    }

    /// <summary>
    /// Adds a key-value pair to the pipeline summary with a Markdown-formatted value.
    /// </summary>
    /// <param name="summary">The pipeline summary handle.</param>
    /// <param name="key">The key or label for the item.</param>
    /// <param name="markdownString">The Markdown-formatted value for the item.</param>
    [AspireExport(Description = "Adds a Markdown-formatted value to the pipeline summary")]
    public static void AddMarkdown(this PipelineSummary summary, string key, string markdownString)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(markdownString);

        summary.Add(key, new MarkdownString(markdownString));
    }

    /// <summary>
    /// Creates a reporting task with plain-text status text.
    /// </summary>
    [AspireExport(Description = "Creates a reporting task with plain-text status text")]
    public static Task<IReportingTask> CreateTask(this IReportingStep reportingStep, string statusText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportingStep);
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);

        return reportingStep.CreateTaskAsync(statusText, cancellationToken);
    }

    /// <summary>
    /// Creates a reporting task with Markdown-formatted status text.
    /// </summary>
    [AspireExport(Description = "Creates a reporting task with Markdown-formatted status text")]
    public static Task<IReportingTask> CreateMarkdownTask(this IReportingStep reportingStep, string markdownString, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportingStep);
        ArgumentNullException.ThrowIfNull(markdownString);

        return reportingStep.CreateTaskAsync(new MarkdownString(markdownString), cancellationToken);
    }

    /// <summary>
    /// Logs a plain-text message for the reporting step.
    /// </summary>
    [AspireExport(Description = "Logs a plain-text message for the reporting step")]
    public static void LogStep(this IReportingStep reportingStep, string level, string message)
    {
        ArgumentNullException.ThrowIfNull(reportingStep);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(message);

        reportingStep.Log(ParseLogLevel(level), message);
    }

    /// <summary>
    /// Logs a Markdown-formatted message for the reporting step.
    /// </summary>
    [AspireExport(Description = "Logs a Markdown-formatted message for the reporting step")]
    public static void LogStepMarkdown(this IReportingStep reportingStep, string level, string markdownString)
    {
        ArgumentNullException.ThrowIfNull(reportingStep);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(markdownString);

        reportingStep.Log(ParseLogLevel(level), new MarkdownString(markdownString));
    }

    /// <summary>
    /// Completes the reporting step with plain-text completion text.
    /// </summary>
    [AspireExport(Description = "Completes the reporting step with plain-text completion text")]
    public static Task CompleteStep(this IReportingStep reportingStep, string completionText, string completionState = "completed", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportingStep);
        ArgumentNullException.ThrowIfNull(completionText);

        return reportingStep.CompleteAsync(completionText, ParseCompletionState(completionState), cancellationToken);
    }

    /// <summary>
    /// Completes the reporting step with Markdown-formatted completion text.
    /// </summary>
    [AspireExport(Description = "Completes the reporting step with Markdown-formatted completion text")]
    public static Task CompleteStepMarkdown(this IReportingStep reportingStep, string markdownString, string completionState = "completed", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportingStep);
        ArgumentNullException.ThrowIfNull(markdownString);

        return reportingStep.CompleteAsync(new MarkdownString(markdownString), ParseCompletionState(completionState), cancellationToken);
    }

    /// <summary>
    /// Updates the reporting task with plain-text status text.
    /// </summary>
    [AspireExport(Description = "Updates the reporting task with plain-text status text")]
    public static Task UpdateTask(this IReportingTask reportingTask, string statusText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportingTask);
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);

        return reportingTask.UpdateAsync(statusText, cancellationToken);
    }

    /// <summary>
    /// Updates the reporting task with Markdown-formatted status text.
    /// </summary>
    [AspireExport(Description = "Updates the reporting task with Markdown-formatted status text")]
    public static Task UpdateTaskMarkdown(this IReportingTask reportingTask, string markdownString, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportingTask);
        ArgumentNullException.ThrowIfNull(markdownString);

        return reportingTask.UpdateAsync(new MarkdownString(markdownString), cancellationToken);
    }

    /// <summary>
    /// Completes the reporting task with plain-text completion text.
    /// </summary>
    [AspireExport(Description = "Completes the reporting task with plain-text completion text")]
    public static Task CompleteTask(this IReportingTask reportingTask, string? completionMessage = null, string completionState = "completed", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportingTask);

        return reportingTask.CompleteAsync(completionMessage, ParseCompletionState(completionState), cancellationToken);
    }

    /// <summary>
    /// Completes the reporting task with Markdown-formatted completion text.
    /// </summary>
    [AspireExport(Description = "Completes the reporting task with Markdown-formatted completion text")]
    public static Task CompleteTaskMarkdown(this IReportingTask reportingTask, string markdownString, string completionState = "completed", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportingTask);
        ArgumentNullException.ThrowIfNull(markdownString);

        return reportingTask.CompleteAsync(new MarkdownString(markdownString), ParseCompletionState(completionState), cancellationToken);
    }

    private static CompletionState ParseCompletionState(string completionState)
    {
        ArgumentNullException.ThrowIfNull(completionState);

        return completionState.ToLowerInvariant() switch
        {
            "inprogress" or "in_progress" or "in-progress" => CompletionState.InProgress,
            "completed" => CompletionState.Completed,
            "completedwithwarning" or "completed_with_warning" or "completed-with-warning" => CompletionState.CompletedWithWarning,
            "completedwitherror" or "completed_with_error" or "completed-with-error" => CompletionState.CompletedWithError,
            _ => throw new ArgumentOutOfRangeException(nameof(completionState), completionState, "Unsupported completion state.")
        };
    }

    private static LogLevel ParseLogLevel(string level)
    {
        return LoggingExports.ParseLogLevel(level, throwOnUnknown: true);
    }
}
