// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Projects a configuration candidate into per-project native restore settings.
/// </summary>
internal sealed class DotNetAppHostRestorePreview : IDisposable
{
    private readonly List<NuGetPackageOperationConfiguration> _configurations = [];
    private readonly TemporaryNuGetConfigFile _replacement;
    private FileInfo? _targetsFile;

    private DotNetAppHostRestorePreview(TemporaryNuGetConfigFile replacement)
    {
        _replacement = replacement;
    }

    internal FileInfo TargetsFile => _targetsFile ?? throw new InvalidOperationException("The restore preview has not been initialized.");

    internal NuGetPackageOperationConfiguration RootConfiguration => _configurations[0];

    internal static async Task<DotNetAppHostRestorePreview> CreateAsync(
        BundleNuGetService nuGetService,
        DotNetAppHostNuGetConfigMergerCandidate candidate,
        DotNetRestoreSettings restoreSettings,
        FileInfo rootProject,
        IReadOnlyDictionary<string, string> projectRestoreTargets,
        CancellationToken cancellationToken)
    {
        if (!BundleNuGetService.IsAncestorDirectory(candidate.TargetFile.DirectoryName!, rootProject.DirectoryName!))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture, UpdateCommandStrings.NuGetConfigOutsideHierarchyFormat,
                candidate.TargetFile.FullName, rootProject.DirectoryName));
        }

        var replacement = await TemporaryNuGetConfigFile.CreatePreviewAsync(
            candidate.TargetFile, candidate.ProposedContent, cancellationToken);
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

            foreach (var path in new[] { rootProject.FullName }.Concat(projectRestoreTargets.Keys).Distinct(StringComparers.FileSystemPath))
            {
                // A selected ancestor config also affects children that inherit it. Evaluate its
                // replacement at native precedence, retaining every child's more-local policy.
                if (!BundleNuGetService.IsAncestorDirectory(candidate.TargetFile.DirectoryName!, Path.GetDirectoryName(path)!))
                {
                    continue;
                }
                var configuration = await nuGetService.CreateConfigurationPreviewAsync(
                    new DirectoryInfo(Path.GetDirectoryName(path)!),
                    candidate.TargetFile, replacement.ConfigFile, cancellationToken);
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
                target));
            preview._targetsFile = new FileInfo(Path.Combine(
                preview.RootConfiguration.EffectiveWorkingDirectory.FullName, "Aspire.CandidateRestore.targets"));
            await File.WriteAllTextAsync(preview.TargetsFile.FullName, document.ToString(), cancellationToken);
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    private static string ProjectCondition(string path)
        => $"('$(MSBuildProjectFullPath)' == '{MSBuildEscaping.Escape(path)}' Or '$(MSBuildProjectFullPath)' == '{MSBuildEscaping.Escape(PathNormalizer.ResolveToFilesystemPath(path))}')";

    public void Dispose()
    {
        foreach (var configuration in _configurations)
        {
            configuration.Dispose();
        }
        _replacement.Dispose();
    }
}
