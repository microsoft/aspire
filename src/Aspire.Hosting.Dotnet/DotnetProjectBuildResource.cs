// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Builds the traditional .NET projects in a distributed application before they start.
/// </summary>
internal sealed class DotnetProjectBuildResource : ExecutableResource, IDisposable
{
    private readonly object _lock = new();
    private readonly List<string> _projectPaths = [];
    private readonly Dictionary<string, string> _projectPathsByIdentity = new(StringComparer.Ordinal);
    private readonly DotnetProjectBuildArtifactManager _artifactManager;
    private bool _buildProjectGenerationStarted;
    private string? _directProjectPath;

    public DotnetProjectBuildResource(string name, string workingDirectory, TimeProvider timeProvider)
        : this(name, workingDirectory, Path.Combine(workingDirectory, ".aspire", "build"), timeProvider)
    {
    }

    internal DotnetProjectBuildResource(
        string name,
        string workingDirectory,
        string buildDirectory,
        TimeProvider timeProvider)
        : base(name, "dotnet", workingDirectory)
    {
        BuildDirectory = buildDirectory;
        _artifactManager = new DotnetProjectBuildArtifactManager(BuildDirectory, timeProvider);
    }

    /// <summary>
    /// Gets the AppHost-local directory that contains generated build projects.
    /// </summary>
    public string BuildDirectory { get; }

    /// <summary>
    /// Gets the project paths included in the generated build project.
    /// </summary>
    public IReadOnlyList<string> ProjectPaths
    {
        get
        {
            lock (_lock)
            {
                return [.. _projectPaths];
            }
        }
    }

    /// <summary>
    /// Adds a project to the generated build project and returns the path used by the coordinated build.
    /// </summary>
    public string AddProject(string projectPath)
    {
        var fullPath = PathNormalizer.ResolveToFilesystemPath(Path.GetFullPath(projectPath));
        // Keep the first path spelling in the traversal project so it stays relative to the AppHost directory,
        // but deduplicate by physical identity so a symlink alias cannot build the same project twice.
        // Every resource for that identity must launch this returned path so MSBuild uses the same
        // project directory, intermediate outputs, and final output that the coordinated build used.
        var projectIdentity = PathNormalizer.ResolveSymlinks(fullPath);

        lock (_lock)
        {
            if (_buildProjectGenerationStarted)
            {
                throw new InvalidOperationException("Projects cannot be added after the coordinated build project has been generated.");
            }

            if (_projectPathsByIdentity.TryGetValue(projectIdentity, out var coordinatedProjectPath))
            {
                return coordinatedProjectPath;
            }

            _projectPathsByIdentity.Add(projectIdentity, fullPath);
            _projectPaths.Add(fullPath);
            return fullPath;
        }
    }

    internal void ConfigureTraversalBuild(IEnumerable<string> projectPaths, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        lock (_lock)
        {
            ThrowIfGenerationStarted();
            _directProjectPath = null;
            _projectPaths.Clear();
            _projectPathsByIdentity.Clear();
            foreach (var projectPath in projectPaths)
            {
                AddProject(projectPath);
            }

            SetWorkingDirectory(workingDirectory);
        }
    }

    internal void ConfigureDirectBuild(string projectPath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        lock (_lock)
        {
            ThrowIfGenerationStarted();
            _projectPaths.Clear();
            _projectPathsByIdentity.Clear();
            _directProjectPath = AddProject(projectPath);
            SetWorkingDirectory(workingDirectory);
        }
    }

    internal Task<string> GetBuildTargetPathAsync(ILogger logger, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_directProjectPath is not null)
            {
                _buildProjectGenerationStarted = true;
                return Task.FromResult(_directProjectPath);
            }
        }

        return WriteBuildProjectAsync(logger, cancellationToken);
    }

    /// <summary>
    /// Writes the generated traversal project to the AppHost-local build directory.
    /// </summary>
    public Task<string> WriteBuildProjectAsync(ILogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projectPaths;
        lock (_lock)
        {
            _buildProjectGenerationStarted = true;
            projectPaths = [.. _projectPaths];
        }

        // The argument callback already caches successful evaluation for one start attempt. Regenerate here
        // on later attempts so cancellation, transient I/O failures, or cache cleanup cannot poison restarts.
        return WriteBuildProjectCoreAsync(projectPaths, logger, cancellationToken);
    }

    /// <summary>
    /// Registers process-lifetime cleanup for generated build-project leases.
    /// </summary>
    public void RegisterForShutdown(IHostApplicationLifetime applicationLifetime)
    {
        _artifactManager.RegisterForShutdown(applicationLifetime);
    }

    internal bool IsBuildProjectLeaseActive(string hash) =>
        _artifactManager.IsLeaseActive(hash);

    /// <inheritdoc/>
    public void Dispose()
    {
        _artifactManager.Dispose();
    }

    private async Task<string> WriteBuildProjectCoreAsync(
        IReadOnlyList<string> projectPaths,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var project = new XDocument(
            new XElement(
                "Project",
                new XAttribute("DefaultTargets", "Build"),
                new XElement(
                    "ItemGroup",
                    projectPaths.Select(projectPath =>
                        new XElement(
                            "ProjectFile",
                            new XAttribute(
                                "Include",
                                EscapeMsBuildPath(NormalizePath(Path.GetRelativePath(BuildDirectory, projectPath))))))),
                CreateTraversalTarget("Restore", buildInParallel: false),
                CreateTraversalTarget("Build", buildInParallel: true)));

        using var projectStream = new MemoryStream();
        var writerSettings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            OmitXmlDeclaration = true,
        };
        using (var writer = XmlWriter.Create(projectStream, writerSettings))
        {
            project.Save(writer);
        }
        var projectBytes = projectStream.ToArray();

        var hash = new XxHash3();
        hash.Append(projectBytes);
        var hashString = Convert.ToHexString(hash.GetCurrentHash())[..12].ToLowerInvariant();

        return await _artifactManager.PublishAndLeaseAsync(
            hashString,
            projectBytes,
            logger,
            cancellationToken).ConfigureAwait(false);
    }

    private static XElement CreateTraversalTarget(string name, bool buildInParallel) =>
        new(
            "Target",
            new XAttribute("Name", name),
            new XElement(
                "MSBuild",
                new XAttribute("Projects", "@(ProjectFile)"),
                new XAttribute("Targets", name),
                new XAttribute("BuildInParallel", buildInParallel)));

    private static string NormalizePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static string EscapeMsBuildPath(string path)
    {
        var builder = new StringBuilder(path.Length);
        foreach (var character in path)
        {
            builder.Append(character switch
            {
                '%' => "%25",
                '$' => "%24",
                '@' => "%40",
                '(' => "%28",
                ')' => "%29",
                '*' => "%2A",
                '\'' => "%27",
                ';' => "%3B",
                '?' => "%3F",
                _ => character,
            });
        }

        return builder.ToString();
    }

    private void SetWorkingDirectory(string workingDirectory)
    {
        var executableAnnotation = Annotations.OfType<ExecutableAnnotation>().Single();
        executableAnnotation.WorkingDirectory = workingDirectory;
    }

    private void ThrowIfGenerationStarted()
    {
        if (_buildProjectGenerationStarted)
        {
            throw new InvalidOperationException("The coordinated build cannot be reconfigured after build-project generation starts.");
        }
    }
}
