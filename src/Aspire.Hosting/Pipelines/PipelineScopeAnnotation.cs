// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// An annotation that resolves the current execution scope from the CI environment.
/// </summary>
/// <remarks>
/// <para>
/// Apply this annotation to an <see cref="IPipelineEnvironment"/> resource to enable automatic
/// scope detection during pipeline execution. When a scope is detected, the executor enters
/// continuation mode: only steps assigned to the current scope execute, while steps from other
/// scopes are restored from state.
/// </para>
/// <para>
/// Each pipeline provider supplies its own resolution logic. For example, a GitHub Actions provider
/// reads <c>GITHUB_RUN_ID</c>, <c>GITHUB_RUN_ATTEMPT</c>, and <c>GITHUB_JOB</c> to produce a
/// <see cref="PipelineScopeResult"/> with run-level isolation and job-level step filtering.
/// </para>
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineScopeAnnotation(
    Func<PipelineScopeContext, Task<PipelineScopeResult?>> resolveAsync) : IResourceAnnotation
{
    /// <summary>
    /// Resolves the current pipeline execution scope from the environment.
    /// </summary>
    /// <param name="context">The context for the scope resolution.</param>
    /// <returns>
    /// A <see cref="PipelineScopeResult"/> containing the run and job identifiers if running
    /// in a recognized CI environment; otherwise, <c>null</c>.
    /// </returns>
    public Task<PipelineScopeResult?> ResolveAsync(PipelineScopeContext context) => resolveAsync(context);
}
