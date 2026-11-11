// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Identifies the project output produced by an externally coordinated build.
/// </summary>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ProjectBuildTarget
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectBuildTarget"/> class.
    /// </summary>
    /// <param name="targetPath">The fully-qualified MSBuild <c>TargetPath</c>.</param>
    /// <param name="useAppHost">
    /// A value indicating whether the project output includes an apphost executable that launches
    /// <paramref name="targetPath"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="targetPath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetPath"/> is empty or consists only of white-space characters.</exception>
    public ProjectBuildTarget(string targetPath, bool useAppHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        TargetPath = targetPath;
        UseAppHost = useAppHost;
    }

    /// <summary>
    /// Gets the fully-qualified MSBuild <c>TargetPath</c>.
    /// </summary>
    public string TargetPath { get; }

    /// <summary>
    /// Gets a value indicating whether the project output includes an apphost executable.
    /// </summary>
    public bool UseAppHost { get; }
}
