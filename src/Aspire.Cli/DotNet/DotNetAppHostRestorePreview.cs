// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Projects a configuration candidate and, for validation restores, isolated restore outputs into
/// per-project native restore settings.
/// </summary>
internal sealed class DotNetAppHostRestorePreview : IDisposable
{
    private readonly List<NuGetPackageOperationConfiguration> _configurations = [];
    private readonly TemporaryNuGetConfigFile? _replacement;
    private FileInfo? _targetsFile;

    private DotNetAppHostRestorePreview(TemporaryNuGetConfigFile? replacement)
    {
        _replacement = replacement;
    }

    internal FileInfo TargetsFile => _targetsFile ?? throw new InvalidOperationException("The restore preview has not been initialized.");

    /// <summary>
    /// Gets the candidate configuration for the root project, or <see langword="null"/> when no
    /// configuration candidate is being previewed.
    /// </summary>
    internal NuGetPackageOperationConfiguration? RootConfiguration => _configurations.FirstOrDefault();

    /// <param name="nuGetService">The NuGet service used to evaluate candidate configuration.</param>
    /// <param name="candidate">The configuration candidate to preview, if any.</param>
    /// <param name="restoreSettings">The root project's evaluated restore settings.</param>
    /// <param name="rootProject">The AppHost project being restored.</param>
    /// <param name="projectRestoreTargets">Each referenced project's evaluated <c>NuGetRestoreTargets</c>.</param>
    /// <param name="isolatedOutputDirectory">
    /// When supplied, every project in the restore graph writes its restore outputs and lockfile
    /// beneath this directory instead of the project tree. Hosts the generated targets file when
    /// there is no configuration candidate.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal static async Task<DotNetAppHostRestorePreview> CreateAsync(
        BundleNuGetService nuGetService,
        DotNetAppHostNuGetConfigMergerCandidate? candidate,
        DotNetRestoreSettings restoreSettings,
        FileInfo rootProject,
        IReadOnlyDictionary<string, string> projectRestoreTargets,
        DirectoryInfo? isolatedOutputDirectory,
        CancellationToken cancellationToken)
    {
        if (candidate is null && isolatedOutputDirectory is null)
        {
            throw new ArgumentException("A configuration candidate or isolated output directory is required.", nameof(candidate));
        }
        if (candidate is not null && !BundleNuGetService.IsAncestorDirectory(candidate.TargetFile.DirectoryName!, rootProject.DirectoryName!))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture, UpdateCommandStrings.NuGetConfigOutsideHierarchyFormat,
                candidate.TargetFile.FullName, rootProject.DirectoryName));
        }

        var replacement = candidate is null
            ? null
            : await TemporaryNuGetConfigFile.CreatePreviewAsync(candidate.TargetFile, candidate.ProposedContent, cancellationToken);
        var preview = new DotNetAppHostRestorePreview(replacement);
        try
        {
            var target = new XElement("Target",
                new XAttribute("Name", "_AspireCandidateRestoreSettings"),
                new XAttribute("BeforeTargets", "_GetRestoreSettings"));
            var imports = new List<XElement>();
            var customProjects = new List<string>();
            // NuGetRestoreTargets is invocation-global. Dispatch the original per-project
            // imports so a referenced project's custom restore targets are not replaced.
            foreach (var (path, restoreTargets) in projectRestoreTargets)
            {
                if (string.IsNullOrEmpty(restoreTargets))
                {
                    continue;
                }
                var condition = ProjectCondition(path);
                customProjects.Add($"!({condition})");
                imports.Add(new XElement("Import",
                    new XAttribute("Project", MSBuildEscaping.Escape(Path.GetFullPath(restoreTargets, Path.GetDirectoryName(path)!))),
                    new XAttribute("Condition", condition)));
            }
            imports.Add(new XElement("Import",
                new XAttribute("Project", MSBuildEscaping.Escape(restoreSettings.RestoreTargets)),
                new XAttribute("Condition", customProjects.Count == 0 ? "true" : string.Join(" And ", customProjects))));

            foreach (var path in candidate is null ? [] : new[] { rootProject.FullName }.Concat(projectRestoreTargets.Keys).Distinct(StringComparers.FileSystemPath))
            {
                // A selected ancestor config also affects children that inherit it. Evaluate its
                // replacement at native precedence, retaining every child's more-local policy.
                if (!BundleNuGetService.IsAncestorDirectory(candidate!.TargetFile.DirectoryName!, Path.GetDirectoryName(path)!))
                {
                    continue;
                }
                var configuration = await nuGetService.CreateConfigurationPreviewAsync(
                    new DirectoryInfo(Path.GetDirectoryName(path)!),
                    candidate.TargetFile, replacement!.ConfigFile, cancellationToken);
                preview._configurations.Add(configuration);
                var identity = string.Equals(path, rootProject.FullName, StringComparisons.FileSystemPath)
                    ? restoreSettings.ProjectIdentity
                    : path;
                target.Add(new XElement("PropertyGroup",
                    new XAttribute("Condition",
                        $"{ProjectCondition(identity)} And '$(RestoreConfigFile)' == '' And '$(RestoreRootConfigDirectory)' == ''"),
                    new XElement("RestoreRootConfigDirectory", MSBuildEscaping.Escape(configuration.EffectiveWorkingDirectory.FullName))));
            }

            var document = new XDocument(new XElement("Project",
                imports,
                target,
                isolatedOutputDirectory is null ? null : CreateIsolatedOutputTarget(isolatedOutputDirectory)));
            preview._targetsFile = new FileInfo(Path.Combine(
                (preview.RootConfiguration?.EffectiveWorkingDirectory ?? isolatedOutputDirectory!).FullName,
                "Aspire.CandidateRestore.targets"));
            await File.WriteAllTextAsync(preview.TargetsFile.FullName, document.ToString(), cancellationToken);
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    private static XElement CreateIsolatedOutputTarget(DirectoryInfo outputDirectory)
    {
        // Validation restores must not change the project tree. NuGet writes project.assets.json,
        // nuget.g.props/targets, the dgspec, and the no-op cache to RestoreOutputPath, and the
        // lockfile to NuGetLockFilePath; both are read by _GenerateRestoreProjectSpec. Running
        // before that target (after its dependencies) lets these per-project overrides apply to
        // every project in the graph, including projects whose own NuGet.targets were dispatched.
        //
        // Validation starts from an existing lockfile so the project's own lockfile policy, including
        // locked mode, applies unchanged; any lockfile NuGet writes stays under the isolated output
        // path. Copy the original from the location NuGet would otherwise use:
        //   1. NuGetLockFilePath, relative to the project directory,
        //   2. an existing packages.<restore project name>.lock.json, with spaces as underscores,
        //   3. packages.lock.json.
        // The restore project name is PackageId, then AssemblyName, then MSBuildProjectName.
        // https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.ProjectModel/ProjectLockFile/PackagesLockFileUtilities.cs
        const string outputRoot = "$(_AspireIsolatedRestoreOutput)";
        return new XElement("Target",
            new XAttribute("Name", "_AspireIsolatedRestoreOutputs"),
            new XAttribute("BeforeTargets", "_GenerateRestoreProjectSpec"),
            new XElement("PropertyGroup",
                new XElement("_AspireIsolatedRestoreOutput",
                    MSBuildEscaping.Escape(Path.TrimEndingDirectorySeparator(outputDirectory.FullName)) + Path.DirectorySeparatorChar
                    + "$([MSBuild]::StableStringHash('$(MSBuildProjectFullPath)'))" + Path.DirectorySeparatorChar),
                new XElement("_AspireRestoreProjectName", "$(MSBuildProjectName)"),
                new XElement("_AspireRestoreProjectName",
                    new XAttribute("Condition", "'$(PackageReferenceCompatibleProjectStyle)' == 'true' And '$(AssemblyName)' != ''"),
                    "$(AssemblyName)"),
                new XElement("_AspireRestoreProjectName",
                    new XAttribute("Condition", "'$(PackageReferenceCompatibleProjectStyle)' == 'true' And '$(PackageId)' != ''"),
                    "$(PackageId)"),
                new XElement("_AspireProjectLockFile",
                    new XAttribute("Condition", "'$(NuGetLockFilePath)' != ''"),
                    "$([System.IO.Path]::Combine('$(MSBuildProjectDirectory)', '$(NuGetLockFilePath)'))"),
                new XElement("_AspireNamedLockFile",
                    "$(MSBuildProjectDirectory)/packages.$(_AspireRestoreProjectName.Replace(' ', '_')).lock.json"),
                new XElement("_AspireProjectLockFile",
                    new XAttribute("Condition", "'$(_AspireProjectLockFile)' == '' And Exists('$(_AspireNamedLockFile)')"),
                    "$(_AspireNamedLockFile)"),
                new XElement("_AspireProjectLockFile",
                    new XAttribute("Condition", "'$(_AspireProjectLockFile)' == ''"),
                    "$(MSBuildProjectDirectory)/packages.lock.json"),
                new XElement("RestoreOutputPath", outputRoot),
                new XElement("NuGetLockFilePath", outputRoot + "packages.lock.json")),
            new XElement("MakeDir", new XAttribute("Directories", outputRoot)),
            new XElement("Copy",
                new XAttribute("SourceFiles", "$(_AspireProjectLockFile)"),
                new XAttribute("DestinationFiles", "$(NuGetLockFilePath)"),
                new XAttribute("Condition", "Exists('$(_AspireProjectLockFile)')")));
    }

    private static string ProjectCondition(string path)
        => $"('$(MSBuildProjectFullPath)' == '{MSBuildEscaping.Escape(path)}' Or '$(MSBuildProjectFullPath)' == '{MSBuildEscaping.Escape(PathNormalizer.ResolveToFilesystemPath(path))}')";

    public void Dispose()
    {
        foreach (var configuration in _configurations)
        {
            configuration.Dispose();
        }
        _replacement?.Dispose();
    }
}
