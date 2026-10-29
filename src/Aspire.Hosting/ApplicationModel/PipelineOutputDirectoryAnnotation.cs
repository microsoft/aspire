// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using IOPath = System.IO.Path;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Selects a resource's pipeline output directory.
/// </summary>
/// <remarks>
/// The default pipeline output service resolves relative paths against the global output directory.
/// Fully qualified paths select an independent destination. This annotation does not affect temporary directories.
/// </remarks>
public sealed class PipelineOutputDirectoryAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineOutputDirectoryAnnotation"/> class.
    /// </summary>
    /// <param name="path">A relative or fully qualified directory path on the host platform. Relative paths may contain <c>.</c> and <c>..</c> segments.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The path is blank, invalid, or rooted without being fully qualified.</exception>
    public PipelineOutputDirectoryAnnotation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (IOPath.IsPathRooted(path) && !IOPath.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The pipeline output directory must be relative or fully qualified.", nameof(path));
        }

        // Validate syntax without assuming the eventual global output directory or accessing the filesystem.
        try
        {
            _ = IOPath.GetFullPath(path, AppContext.BaseDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The pipeline output directory path must be valid.", nameof(path), exception);
        }

        Path = path;
    }

    /// <summary>
    /// Gets the configured directory path.
    /// </summary>
    public string Path { get; }
}
