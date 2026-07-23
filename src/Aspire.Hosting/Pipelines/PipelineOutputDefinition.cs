// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Declares a named output produced by a pipeline step.
/// </summary>
/// <remarks>
/// Relative paths are resolved from the AppHost project directory. Pipeline steps must write
/// artifacts to the path returned by <see cref="IPipelineOutputResolver.Resolve"/>.
/// </remarks>
[Experimental("ASPIREPIPELINES004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineOutputDefinition
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineOutputDefinition"/> class.
    /// </summary>
    /// <param name="name">The name that uniquely identifies this output within its pipeline step.</param>
    /// <param name="defaultPath">The default logical target path.</param>
    /// <param name="kind">The kind of output.</param>
    public PipelineOutputDefinition(string name, string defaultPath, PipelineOutputKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultPath);

        if (name.Contains(':'))
        {
            throw new ArgumentException("Pipeline output names cannot contain ':'.", nameof(name));
        }

        if (Path.IsPathRooted(defaultPath))
        {
            throw new ArgumentException(
                "The default pipeline output path must be relative to the AppHost directory.",
                nameof(defaultPath));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Name = name;
        DefaultPath = defaultPath;
        Kind = kind;
    }

    /// <summary>
    /// Gets the name that uniquely identifies this output within its pipeline step.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the default logical target path.
    /// </summary>
    /// <remarks>
    /// Relative paths are resolved from <see cref="IPipelineOutputResolver.AppHostDirectory"/>.
    /// The configured path can be overridden through the
    /// <c>Pipeline:Outputs:&lt;step-name&gt;:&lt;output-name&gt;:Path</c> configuration key.
    /// </remarks>
    public string DefaultPath { get; }

    /// <summary>
    /// Gets the kind of output.
    /// </summary>
    public PipelineOutputKind Kind { get; }
}
