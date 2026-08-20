// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Represents metadata about a project resource.
/// </summary>
public interface IProjectMetadata : IResourceAnnotation
{
    /// <summary>
    /// Gets the fully-qualified path to the project or file-based app file.
    /// </summary>
    public string ProjectPath { get; }

    /// <summary>
    /// Gets the launch settings associated with the project.
    /// </summary>
    public LaunchSettings? LaunchSettings => null;

    // Internal for testing.
    internal IConfiguration? Configuration => null;

    /// <summary>
    /// Gets a value indicating whether building the project before running it should be suppressed.
    /// </summary>
    public bool SuppressBuild => false;

    /// <summary>
    /// Gets a value indicating whether the project is a file-based app (a .cs file) rather than a full project (.csproj).
    /// </summary>
    public bool IsFileBasedApp => string.Equals(Path.GetExtension(ProjectPath), ".cs", StringComparison.OrdinalIgnoreCase);
}

[DebuggerDisplay("Type = {GetType().Name,nq}, ProjectPath = {ProjectPath}")]
internal sealed class ProjectMetadata(string projectPath) : IProjectMetadata
{
    private string? _resolvedProjectPath;

    public string ProjectPath => _resolvedProjectPath ??= ProjectPathResolver.ResolveProjectPath(projectPath);

    public bool SuppressBuild => false;
}
