// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Resolves output paths declared by the currently executing pipeline step.
/// </summary>
[Experimental("ASPIREPIPELINES004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IPipelineOutputResolver
{
    /// <summary>
    /// Gets the fully qualified AppHost project directory used to resolve relative output paths.
    /// </summary>
    string AppHostDirectory { get; }

    /// <summary>
    /// Gets the primary pipeline output.
    /// </summary>
    /// <remarks>
    /// The output path is the only path to which the pipeline step should write. The logical
    /// target path identifies where the output persists outside a relocated pipeline execution.
    /// </remarks>
    ResolvedPipelineOutput PrimaryOutput { get; }

    /// <summary>
    /// Resolves a declared output for the current pipeline step.
    /// </summary>
    /// <param name="definition">The output definition attached to the current pipeline step.</param>
    /// <returns>The resolved output paths.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the definition is not declared by the current pipeline step.
    /// </exception>
    ResolvedPipelineOutput Resolve(PipelineOutputDefinition definition);
}
