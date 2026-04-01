// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// An annotation that maps scope identifiers to the pipeline steps assigned to each scope.
/// </summary>
/// <remarks>
/// <para>
/// This annotation is populated by pipeline environment providers after scheduling resolution.
/// For example, the GitHub Actions provider maps job IDs (from <c>GITHUB_JOB</c>) to the step
/// names assigned to each job by the <c>SchedulingResolver</c>.
/// </para>
/// <para>
/// During continuation-mode execution, the pipeline executor uses this mapping to determine
/// which steps should execute in the current scope and which should be restored from state.
/// </para>
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineScopeMapAnnotation(
    IReadOnlyDictionary<string, IReadOnlyList<string>> scopeToSteps) : IResourceAnnotation
{
    /// <summary>
    /// Gets the mapping from scope identifiers to the step names assigned to each scope.
    /// </summary>
    /// <value>
    /// A dictionary where the key is a scope identifier (e.g., a CI job ID) and the value
    /// is the ordered list of step names that should execute within that scope.
    /// </value>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ScopeToSteps { get; } = scopeToSteps;
}
