// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides contextual information for creating pipeline steps from a <see cref="PipelineStepAnnotation"/>.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport(ExposeProperties = true)]
public class PipelineStepFactoryContext
{
    /// <summary>
    /// Gets the pipeline context that has the model and other properties.
    /// </summary>
    public required PipelineContext PipelineContext { get; init; }

    /// <summary>
    /// Gets the resource that this factory is associated with.
    /// </summary>
    public required IResource Resource { get; init; }

    /// <summary>
    /// Gets the pipeline steps that were directly added to the pipeline (not from annotations).
    /// This allows annotation factories to see the existing steps and create synthetic steps
    /// that depend on them.
    /// </summary>
    public IReadOnlyList<PipelineStep> ExistingSteps { get; init; } = [];
}
