// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Project metadata for a C# project or file-based app that was added by path.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, ProjectPath = {ProjectPath}")]
internal sealed class DotnetProjectMetadata(string projectPath, string? buildConfiguration) : IProjectMetadata
{
    private IReadOnlyDictionary<string, string> _buildEnvironment = ReadOnlyDictionary<string, string>.Empty;
    private string? _buildWorkingDirectory;
    private string? _resolvedProjectPath;

    // Resolution is deferred so construction never touches the file system; an unresolvable path is
    // reported as a resource start failure instead.
    public string ProjectPath => _resolvedProjectPath ??= ProjectPathResolver.ResolveProjectPath(projectPath);

    public string? BuildConfiguration { get; } = buildConfiguration;

    public bool SuppressBuild { get; set; }

    public IReadOnlyDictionary<string, string> BuildEnvironment =>
        Volatile.Read(ref _buildEnvironment);

    public string? BuildWorkingDirectory =>
        Volatile.Read(ref _buildWorkingDirectory);

    internal DotnetProjectRunPropertiesResolverCallback RunPropertiesResolver { get; set; } =
        DotnetProjectRunPropertiesResolver.ResolveAsync;

    /// <summary>
    /// Uses the exact path selected by the coordinated build for subsequent process and IDE launches.
    /// </summary>
    internal void SetProjectPath(string coordinatedProjectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(coordinatedProjectPath);
        _resolvedProjectPath = coordinatedProjectPath;
    }

    internal void SetBuildEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var snapshot = new Dictionary<string, string>(environment.Count, comparer);
        foreach (var (name, value) in environment)
        {
            snapshot[name] = value;
        }

        Volatile.Write(ref _buildEnvironment, new ReadOnlyDictionary<string, string>(snapshot));
    }

    internal void SetBuildWorkingDirectory(string? workingDirectory)
    {
        Volatile.Write(ref _buildWorkingDirectory, workingDirectory);
    }
}
