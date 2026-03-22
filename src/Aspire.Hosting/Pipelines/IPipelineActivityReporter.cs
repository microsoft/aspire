// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Interface for reporting publishing activities.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IPipelineActivityReporter
{
    /// <summary>
    /// Creates a new publishing step with the specified title.
    /// </summary>
    /// <param name="title">The title of the publishing step.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The publishing step</returns>
    Task<IReportingStep> CreateStepAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals that the entire publishing process has completed.
    /// </summary>
    /// <param name="options">The options for completing the publishing process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task CompletePublishAsync(PublishCompletionOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals that the entire publishing process has completed.
    /// </summary>
    /// <param name="completionMessage">The completion message of the publishing process.</param>
    /// <param name="completionState">The completion state of the publishing process. When null, the state is automatically aggregated from all steps.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [Obsolete("Use CompletePublishAsync(PublishCompletionOptions?, CancellationToken) instead.")]
    Task CompletePublishAsync(string? completionMessage = null, CompletionState? completionState = null, CancellationToken cancellationToken = default);
}
