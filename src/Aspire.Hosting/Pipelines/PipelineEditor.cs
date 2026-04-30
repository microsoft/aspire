// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

 #pragma warning disable ASPIREPIPELINES001

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides an ATS-first editor for pipeline configuration callbacks.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
internal sealed class PipelineEditor(IReadOnlyList<PipelineStep> steps)
{
    private readonly IReadOnlyList<PipelineStep> _steps = steps ?? throw new ArgumentNullException(nameof(steps));

    /// <summary>
    /// Gets all configured pipeline steps.
    /// </summary>
    /// <returns>The configured pipeline steps.</returns>
    [AspireExport(Description = "Gets all configured pipeline steps")]
    public IReadOnlyList<PipelineStep> Steps() => _steps;

    /// <summary>
    /// Gets all pipeline steps that have the specified tag.
    /// </summary>
    /// <param name="tag">The tag to search for.</param>
    /// <returns>The matching pipeline steps.</returns>
    [AspireExport(Description = "Gets pipeline steps with the specified tag")]
    public IReadOnlyList<PipelineStep> StepsByTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return _steps.Where(s => s.Tags.Contains(tag)).ToArray();
    }
}
