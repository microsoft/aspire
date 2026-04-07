// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides contextual information and services for the pipeline execution process of a distributed application.
/// </summary>
/// <param name="model">The distributed application model the pipeline is running against.</param>
/// <param name="executionContext">The execution context for the distributed application.</param>
/// <param name="serviceProvider">The service provider for dependency resolution.</param>
/// <param name="logger">The logger for pipeline operations.</param>
/// <param name="cancellationToken">The cancellation token for the pipeline operation.</param>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport(ExposeProperties = true)]
public sealed class PipelineContext(
    DistributedApplicationModel model,
    DistributedApplicationExecutionContext executionContext,
    IServiceProvider serviceProvider,
    ILogger logger,
    CancellationToken cancellationToken)
{
    /// <summary>
    /// Gets the distributed application model to be deployed.
    /// </summary>
    public DistributedApplicationModel Model { get; } = model;

    /// <summary>
    /// Gets the execution context for the distributed application.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; } = executionContext;

    /// <summary>
    /// Gets the service provider for dependency resolution.
    /// </summary>
    public IServiceProvider Services { get; } = serviceProvider;

    /// <summary>
    /// Gets the logger for pipeline operations.
    /// </summary>
    public ILogger Logger { get; } = logger;

    /// <summary>
    /// Gets the cancellation token for the pipeline operation.
    /// </summary>
    public CancellationToken CancellationToken { get; set; } = cancellationToken;

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
    /// context.PipelineContext.Summary.Add("☁️ Target", "Azure");
    /// context.PipelineContext.Summary.Add("📦 Resource Group", "rg-myapp");
    /// </code>
    /// </example>
    public PipelineSummary Summary { get; } = new();
}
