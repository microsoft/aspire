// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides contextual information for a specific pipeline step execution.
/// </summary>
/// <remarks>
/// This context combines the shared pipeline context with a step-specific publishing step,
/// allowing each step to track its own tasks and completion state independently.
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport(ExposeProperties = true)]
public sealed class PipelineStepContext
{
    /// <summary>
    /// Gets the pipeline context shared across all steps.
    /// </summary>
    public required PipelineContext PipelineContext { get; init; }

    /// <summary>
    /// Gets the publishing step associated with this specific step execution.
    /// </summary>
    /// <value>
    /// The <see cref="IReportingStep"/> instance that can be used to create tasks and manage the publishing process for this step.
    /// </value>
    public required IReportingStep ReportingStep { get; init; }

    /// <summary>
    /// Gets the distributed application model to be deployed.
    /// </summary>
    public DistributedApplicationModel Model => PipelineContext.Model;

    /// <summary>
    /// Gets the execution context for the distributed application.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext => PipelineContext.ExecutionContext;

    /// <summary>
    /// Gets the service provider for dependency resolution.
    /// </summary>
    public IServiceProvider Services => PipelineContext.Services;

    /// <summary>
    /// Gets the logger for pipeline operations that writes to both the pipeline logger and the step logger.
    /// </summary>
    public ILogger Logger => PipelineContext.Logger;

    /// <summary>
    /// Gets the cancellation token for the pipeline operation.
    /// </summary>
    public CancellationToken CancellationToken => PipelineContext.CancellationToken;

    /// <summary>
    /// Gets the pipeline summary that steps can add information to.
    /// The summary will be displayed to users after pipeline execution completes.
    /// </summary>
    /// <remarks>
    /// Pipeline steps can add key-value pairs to the summary to provide useful information
    /// about the pipeline execution, such as deployment targets, resource names, URLs, etc.
    /// </remarks>
    /// <example>
    /// <code>
    /// // In a pipeline step
    /// context.Summary.Add("☁️ Target", "Azure");
    /// context.Summary.Add("📦 Resource Group", "rg-myapp");
    /// </code>
    /// </example>
    public PipelineSummary Summary => PipelineContext.Summary;
}
