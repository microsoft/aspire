// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using IOPath = System.IO.Path;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Describes a changed output file.
/// </summary>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class PipelineOutputChange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineOutputChange"/> class.
    /// </summary>
    /// <param name="kind">The kind of change.</param>
    /// <param name="path">The reporting path relative to the output directory. Custom coverage may use parent segments.</param>
    /// <exception cref="ArgumentException">The path is blank or invalid.</exception>
    public PipelineOutputChange(PipelineOutputChangeKind kind, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IOPath.IsPathRooted(path) || path.IndexOfAny(IOPath.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("The output file path must be a valid relative path.", nameof(path));
        }
        Kind = kind;
        Path = path;
    }

    /// <summary>
    /// Gets the kind of change.
    /// </summary>
    public PipelineOutputChangeKind Kind { get; }

    /// <summary>
    /// Gets the file path relative to the output directory.
    /// </summary>
    public string Path { get; }
}

/// <summary>
/// Specifies how an output file changed between captures.
/// </summary>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum PipelineOutputChangeKind
{
    /// <summary>
    /// The file exists only in the later capture.
    /// </summary>
    Added,

    /// <summary>
    /// An existing file is reported as changed.
    /// </summary>
    Changed,

    /// <summary>
    /// The file exists only in the earlier capture.
    /// </summary>
    Removed
}
