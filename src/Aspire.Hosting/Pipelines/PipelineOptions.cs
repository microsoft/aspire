// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents the options for executing a pipeline.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class PipelineOptions
{
    /// <summary>
    /// Gets or sets the path to the directory where the pipeline output will be written.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to clear the deployment cache.
    /// When true, deployment state will not be saved or used.
    /// </summary>
    public bool ClearCache { get; set; }

    /// <summary>
    /// Gets or sets the name of a specific pipeline step to run.
    /// When specified, only this step and its dependencies will be executed.
    /// </summary>
    public string? Step { get; set; }

    /// <summary>
    /// Gets or sets the minimum log level for pipeline execution.
    /// </summary>
    public string? LogLevel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to skip confirmation prompts for destructive operations.
    /// </summary>
    public bool SkipConfirmation { get; set; }
}
