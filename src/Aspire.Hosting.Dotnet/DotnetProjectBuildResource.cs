// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Builds .NET projects and file-based apps in a distributed application before they start.
/// </summary>
internal sealed class DotnetProjectBuildResource : ExecutableResource, IDisposable
{
    private const string BuildLogsDirectoryName = "logs";

    private readonly object _lock = new();
    private readonly List<string> _projectPaths = [];
    private readonly Dictionary<string, string> _projectPathsByIdentity = new(StringComparer.Ordinal);
    private readonly DotnetProjectBuildArtifactManager _artifactManager;
    private bool _buildProjectGenerationStarted;
    private bool _restoreProjectsIndividually;
    private string? _buildConfiguration;
    private string? _directProjectPath;

    internal DotnetProjectBuildResource(
        string name,
        string workingDirectory,
        string buildDirectory,
        TimeProvider timeProvider)
        : base(name, "dotnet", ResolveBuildPathCasing(workingDirectory))
    {
        BuildDirectory = ResolveBuildPathCasing(buildDirectory);
        _artifactManager = new DotnetProjectBuildArtifactManager(BuildDirectory, timeProvider);
    }

    /// <summary>
    /// Gets the AppHost intermediate-output directory that contains generated build projects.
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

    internal string? BuildConfiguration
    {
        get
        {
            lock (_lock)
            {
                return _buildConfiguration;
            }
        }
    }

    /// <summary>
    /// Gets whether the AppHost configured the traversal to restore each root project separately.
    /// </summary>
    internal bool RestoreProjectsIndividually
    {
        get
        {
            lock (_lock)
            {
                return _restoreProjectsIndividually;
            }
        }
    }

    /// <summary>
    /// Adds a project to the generated build project and returns the path used by the coordinated build.
    /// </summary>
    public string AddProject(string projectPath)
    {
        var fullPath = ResolveBuildPathCasing(projectPath);
        // Keep the first path spelling in the traversal project so it stays relative to the AppHost directory,
        // but deduplicate by physical identity so a symlink alias cannot build the same project twice.
        // Every resource for that identity must launch this returned path so MSBuild uses the same
        // project directory, intermediate outputs, and final output that the coordinated build used.
        var projectIdentity = PathNormalizer.ResolveToFilesystemPath(fullPath);

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

    internal void ConfigureTraversalBuild(
        IEnumerable<string> projectPaths,
        string workingDirectory,
        string? buildConfiguration,
        bool restoreProjectsIndividually)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        lock (_lock)
        {
            ThrowIfGenerationStarted();
            _buildConfiguration = buildConfiguration;
            _restoreProjectsIndividually = restoreProjectsIndividually;
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

    internal void ConfigureDirectBuild(
        string projectPath,
        string workingDirectory,
        string? buildConfiguration)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        lock (_lock)
        {
            ThrowIfGenerationStarted();
            _buildConfiguration = buildConfiguration;
            _restoreProjectsIndividually = false;
            _projectPaths.Clear();
            _projectPathsByIdentity.Clear();
            _directProjectPath = AddProject(projectPath);
            SetWorkingDirectory(workingDirectory);
        }
    }

    internal void SetBuildConfiguration(string? buildConfiguration)
    {
        lock (_lock)
        {
            ThrowIfGenerationStarted();
            _buildConfiguration = buildConfiguration;
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
    /// Writes the generated traversal project to the AppHost intermediate-output build directory.
    /// </summary>
    public Task<string> WriteBuildProjectAsync(ILogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projectPaths;
        bool restoreProjectsIndividually;
        lock (_lock)
        {
            _buildProjectGenerationStarted = true;
            projectPaths = [.. _projectPaths];
            restoreProjectsIndividually = _restoreProjectsIndividually;
        }

        // The argument callback already caches successful evaluation for one start attempt. Regenerate here
        // on later attempts so cancellation, transient I/O failures, or cache cleanup cannot poison restarts.
        return WriteBuildProjectCoreAsync(projectPaths, restoreProjectsIndividually, logger, cancellationToken);
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

    /// <summary>
    /// Gets the file that this AppHost's coordinated build writes its MSBuild file log to, or <see langword="null"/>
    /// when MSBuild cannot address the build directory in logger parameters.
    /// </summary>
    /// <remarks>
    /// MSBuild's file logger keeps this file open with write access for the lifetime of the build process, so an
    /// open handle on any log in this directory marks a coordinated build that is still running. That build can
    /// outlive the AppHost that launched it: <c>aspire stop</c> returns once the AppHost exits, DCP cleans up
    /// afterwards, and <c>dotnet build</c> keeps running after SIGTERM. Each AppHost process uses its own log file so
    /// a build never opens a log that another build still holds.
    /// </remarks>
    internal string? BuildLogPath =>
        // MSBuild splits logger parameters on ';' and '=' and does not honor quoting in LogFile.
        BuildDirectory.AsSpan().IndexOfAny(';', '=') >= 0
            ? null
            : Path.Combine(BuildDirectory, BuildLogsDirectoryName, $"{Environment.ProcessId}.{Name}.log");

    /// <summary>
    /// Gets or sets how long <see cref="WaitForRunningBuildsAsync"/> waits for other builds before building anyway.
    /// </summary>
    internal TimeSpan RunningBuildWaitTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Waits until no other coordinated build is writing a log in <see cref="BuildDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Two builds of the same projects write the same intermediate outputs, so a build started while a previous
    /// AppHost's build is still running can fail with sharing violations (for example in static web asset
    /// compression). Waiting for the previous build is enough: once it finishes, this build is incremental. When
    /// waiting times out, this build proceeds anyway.
    /// </remarks>
    internal async Task WaitForRunningBuildsAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (BuildLogPath is not { } buildLogPath ||
            Path.GetDirectoryName(buildLogPath) is not { } logsDirectory ||
            !Directory.Exists(logsDirectory))
        {
            return;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        var waitingLogged = false;
        foreach (var runningBuildLog in Directory.GetFiles(logsDirectory, "*.log"))
        {
            // Acquiring a log that no build holds deletes it.
            using var buildLog = FileLock.TryAcquire(runningBuildLog);
            if (buildLog is not null)
            {
                continue;
            }

            if (!waitingLogged)
            {
                waitingLogged = true;
                logger.LogInformation(
                    "Waiting for a coordinated build that is still running in '{BuildDirectory}' to finish before building.",
                    BuildDirectory);
            }

            var remaining = RunningBuildWaitTimeout - Stopwatch.GetElapsedTime(startTimestamp);
            try
            {
                using var finishedBuildLog = await FileLock.AcquireAsync(
                    runningBuildLog,
                    cancellationToken,
                    remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                logger.LogWarning(
                    ex,
                    "A coordinated build in '{BuildDirectory}' is still running after {Timeout}. Building anyway.",
                    BuildDirectory,
                    RunningBuildWaitTimeout);
                return;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _artifactManager.Dispose();
    }

    private async Task<string> WriteBuildProjectCoreAsync(
        IReadOnlyList<string> projectPaths,
        bool restoreProjectsIndividually,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // NuGet can restore all entry projects in one graph without introducing solution build properties.
        // See https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Build.Tasks/NuGet.targets.
        // Import after the per-project restore target so NuGet overrides it only when aggregate restore is selected
        // and the selected NuGet targets are available.
        // Static restore needs a restore-capable entry project, which this SDK-less wrapper is not.
        //
        // NuGet documents RestoreGraphProjectInput as a property. The ProjectFile transform intentionally remains
        // literal during evaluation and expands when NuGet consumes the property inside _LoadRestoreGraphEntryPoints.
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
                new XElement(
                    "PropertyGroup",
                    new XElement("RestoreGraphProjectInput", "@(ProjectFile->'%(FullPath)')"),
                    restoreProjectsIndividually ? null : new XElement(
                        "NuGetRestoreTargets",
                        new XAttribute("Condition", "'$(NuGetRestoreTargets)' == ''"),
                        "$(MSBuildToolsPath)/NuGet.targets")),
                CreateRestoreTarget(restoreProjectsIndividually),
                restoreProjectsIndividually ? null : new XElement(
                    "Import",
                    new XAttribute("Project", "$(NuGetRestoreTargets)"),
                    new XAttribute(
                        "Condition",
                        "'$(RestoreUseStaticGraphEvaluation)' != 'true' and Exists('$(NuGetRestoreTargets)')")),
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

    private static XElement CreateRestoreTarget(bool restoreProjectsIndividually)
    {
        var target = CreateTraversalTarget("Restore", buildInParallel: false);
        if (restoreProjectsIndividually)
        {
            target.AddFirst(new XElement(
                "Message",
                new XAttribute("Importance", "high"),
                new XAttribute("Text", "Restoring projects individually as configured by the AppHost.")));
        }
        else
        {
            target.AddFirst(new[]
            {
                new XElement(
                    "Message",
                    new XAttribute("Importance", "high"),
                    new XAttribute("Text", "Restoring projects individually because RestoreUseStaticGraphEvaluation is enabled."),
                    new XAttribute("Condition", "'$(RestoreUseStaticGraphEvaluation)' == 'true'")),
                new XElement(
                    "Message",
                    new XAttribute("Importance", "high"),
                    new XAttribute("Text", "Restoring projects individually because the selected NuGet restore targets are unavailable."),
                    new XAttribute(
                        "Condition",
                        "'$(RestoreUseStaticGraphEvaluation)' != 'true' and !Exists('$(NuGetRestoreTargets)')")),
            });
        }

        return target;
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
        executableAnnotation.WorkingDirectory = ResolveBuildPathCasing(workingDirectory);
    }

    // The AppHost directory keeps whatever spelling the AppHost was built or launched with, such as the lowercase
    // drive letter VS Code uses on Windows. The static web assets SDK compares paths ordinally, so when one build
    // path uses that spelling and another uses the on-disk spelling, Blazor WebAssembly builds synthesize framework
    // asset paths that do not exist and fail (https://github.com/microsoft/aspire/issues/21621). Pass only on-disk
    // spellings to the build. Symlinks are intentionally preserved.
    internal static string ResolveBuildPathCasing(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var existingPath = fullPath;
        while (!Path.Exists(existingPath))
        {
            var parent = Path.GetDirectoryName(existingPath);
            if (parent is null)
            {
                return fullPath;
            }

            existingPath = parent;
        }

        var resolvedExistingPath = PathNormalizer.ResolvePathCasing(existingPath);
        return existingPath.Length == fullPath.Length
            ? resolvedExistingPath
            : Path.Join(resolvedExistingPath, fullPath[existingPath.Length..]);
    }

    private void ThrowIfGenerationStarted()
    {
        if (_buildProjectGenerationStarted)
        {
            throw new InvalidOperationException("The coordinated build cannot be reconfigured after build-project generation starts.");
        }
    }
}
