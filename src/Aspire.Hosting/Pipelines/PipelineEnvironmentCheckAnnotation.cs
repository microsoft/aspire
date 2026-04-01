// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// An annotation that provides a relevance check for a pipeline environment resource.
/// </summary>
/// <remarks>
/// Apply this annotation to an <see cref="IPipelineEnvironment"/> resource to indicate
/// under what conditions the environment is active for the current invocation. For example,
/// a GitHub Actions environment might check for the <c>GITHUB_ACTIONS</c> environment variable.
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineEnvironmentCheckAnnotation(
    Func<PipelineEnvironmentCheckContext, Task<bool>> checkAsync) : IResourceAnnotation
{
    /// <summary>
    /// Evaluates whether the pipeline environment is relevant for the current invocation.
    /// </summary>
    /// <param name="context">The context for the check.</param>
    /// <returns>A task that resolves to <c>true</c> if this environment is relevant; otherwise, <c>false</c>.</returns>
    public Task<bool> CheckAsync(PipelineEnvironmentCheckContext context) => checkAsync(context);
}
