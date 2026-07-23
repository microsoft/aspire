// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents a pipeline output after its configured and effective paths have been resolved.
/// </summary>
[Experimental("ASPIREPIPELINES004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ResolvedPipelineOutput
{
    internal ResolvedPipelineOutput(
        string publisherName,
        string name,
        PipelineOutputKind kind,
        string outputPath,
        string logicalTargetPath)
    {
        PublisherName = publisherName;
        Name = name;
        Kind = kind;
        OutputPath = outputPath;
        LogicalTargetPath = logicalTargetPath;
    }

    internal string PublisherName { get; }

    internal string Name { get; }

    /// <summary>
    /// Gets the kind of output.
    /// </summary>
    public PipelineOutputKind Kind { get; }

    /// <summary>
    /// Gets the path where the pipeline step must write the output.
    /// </summary>
    /// <remarks>
    /// This path can differ from <see cref="LogicalTargetPath"/> when a caller redirects output,
    /// such as when verifying generated artifacts without modifying their checked-in targets.
    /// </remarks>
    public string OutputPath { get; }

    /// <summary>
    /// Gets the configured logical target path for the output.
    /// </summary>
    /// <remarks>
    /// Use this path when generated content needs to refer to its eventual destination. Always use
    /// <see cref="OutputPath"/> for file-system writes.
    /// </remarks>
    public string LogicalTargetPath { get; }
}
