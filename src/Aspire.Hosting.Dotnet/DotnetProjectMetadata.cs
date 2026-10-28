// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Project metadata for a .NET project or file-based C# app that was added by path.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, ProjectPath = {ProjectPath}")]
internal sealed class DotnetProjectMetadata(string projectPath, string? buildConfiguration, string resourceName) : IProjectMetadata
{
    private IReadOnlyDictionary<string, string> _buildEnvironment = ReadOnlyDictionary<string, string>.Empty;
    private string? _buildWorkingDirectory;
    private string? _resolvedProjectPath;

    // Keep directory shorthand stable if the directory contents change after the resource is added.
    public string ProjectPath => _resolvedProjectPath ??=
        ProjectPathResolver.ResolveProjectPath(projectPath, includeAllDotnetProjectTypes: true);

    public LaunchSettings? LaunchSettings
    {
        get
        {
            if (!File.Exists(ProjectPath))
            {
                // The shared loader rejects non-file paths eagerly. Explain unresolved paths here,
                // but keep existing unsupported files on the resource-start validation path.
                ValidateProjectPath();
                return null;
            }

            if (!ProjectPath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var launchSettingsPath = Path.Combine(Path.GetDirectoryName(ProjectPath)!, "My Project", "launchSettings.json");
            if (!File.Exists(launchSettingsPath))
            {
                return null;
            }

            // Supply VB's preferred file through metadata so the shared service loader keeps its legacy
            // Properties fallback and file-app .run.json behavior. A present JSON "null" document must
            // still take precedence over Properties rather than exposing the lower-priority profiles.
            return LaunchSettingsReader.ReadLaunchSettingsFile(launchSettingsPath, $"project resource '{resourceName}'")
                ?? new LaunchSettings();
        }
    }

    public string? BuildConfiguration { get; } = buildConfiguration;

    public bool SuppressBuild { get; set; }

    public IReadOnlyDictionary<string, string> BuildEnvironment =>
        Volatile.Read(ref _buildEnvironment);

    public string? BuildWorkingDirectory =>
        Volatile.Read(ref _buildWorkingDirectory);

    internal DotnetProjectRunPropertiesResolverCallback RunPropertiesResolver { get; set; } =
        DotnetProjectRunPropertiesResolver.ResolveAsync;

    internal void ValidateProjectPath()
    {
        var path = ProjectPath;
        if (ProjectPathResolver.IsSupportedDotnetProjectFile(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var message = Directory.Exists(path)
            ? $"Path to .NET project for resource '{resourceName}' could not be determined. The directory '{path}' must contain exactly one .csproj, .fsproj, or .vbproj file. Specify an explicit project filename when multiple supported projects exist."
            : $"The .NET app path '{path}' for resource '{resourceName}' is invalid. The path must be to a .cs file, an SDK-style .csproj, .fsproj, or .vbproj file, or a directory containing exactly one supported project.";
        throw new DistributedApplicationException(message);
    }

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
