// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using NuGet.Configuration;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Maintains channel-owned entries and additive integration mappings without replacing user policy.
/// </summary>
internal sealed class DotNetAppHostNuGetConfigMerger(BundleNuGetService nuGetService)
{
    internal async Task CreateOrUpdateAsync(
        DirectoryInfo targetDirectory,
        NuGetConfiguration configuration,
        bool createIfMissing,
        string? globalPackagesFolder,
        Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback,
        CancellationToken cancellationToken)
    {
        var candidate = await PrepareAsync(
            targetDirectory, configuration, createIfMissing, globalPackagesFolder, cancellationToken);
        if (candidate is null ||
            confirmationCallback is not null &&
            !await confirmationCallback(
                candidate.TargetFile,
                candidate.GetOriginalDocument(),
                candidate.GetProposedDocument(),
                cancellationToken))
        {
            return;
        }

        await ApplyAsync(candidate, cancellationToken);
    }

    internal async Task<DotNetAppHostNuGetConfigMergerCandidate?> PrepareAsync(
        DirectoryInfo targetDirectory,
        NuGetConfiguration configuration,
        bool createIfMissing,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.Overlay is null)
        {
            return null;
        }

        FileInfo? targetFile = null;
        var targetExists = targetDirectory.Exists &&
            TryFindNuGetConfigInDirectory(targetDirectory, out targetFile);
        if (!targetExists && !createIfMissing)
        {
            return null;
        }

        targetFile ??= new FileInfo(Path.Combine(targetDirectory.FullName, "nuget.config"));
        byte[]? originalContent = targetExists
            ? await File.ReadAllBytesAsync(targetFile.FullName, cancellationToken)
            : null;
        if (originalContent is not null && globalPackagesFolder == CliPathHelper.StagingNuGetPackagesFolderName)
        {
            var document = new XmlDocument();
            using var stream = new MemoryStream(originalContent, writable: false);
            document.Load(stream);
            if (document.SelectNodes("/configuration/config/add")!.OfType<XmlElement>()
                .Any(static element => string.Equals(element.GetAttribute("key"), "globalPackagesFolder", StringComparison.OrdinalIgnoreCase)))
            {
                globalPackagesFolder = null;
            }
        }

        // Discovery can narrow an already sufficient mapping policy without needing any
        // persisted edits. Do not materialize an empty child configuration in that case.
        var reusesExistingMappings = configuration.Settings.PackageSourceMappings.Count > 0 &&
            configuration.AppHostConfig is
            {
                SourceChanges.Sources.Count: 0,
                SourceChanges.RetiredSourceKeys.Count: 0,
                PackageSourceMappings.Count: 0,
                RemovedPackageSourceMappings.Count: 0
            };
        if ((!configuration.HasSourcePolicyChanges || reusesExistingMappings) && globalPackagesFolder is null)
        {
            return null;
        }

        var proposedContent = await CreatePersistentNuGetConfigContentAsync(
            configuration,
            targetFile,
            originalContent is null ? default(ReadOnlyMemory<byte>?) : new ReadOnlyMemory<byte>(originalContent),
            globalPackagesFolder,
            cancellationToken);
        if (originalContent is not null && proposedContent.AsSpan().SequenceEqual(originalContent))
        {
            return null;
        }

        return new DotNetAppHostNuGetConfigMergerCandidate(targetFile, originalContent, proposedContent);
    }

    internal async Task<byte[]> CreatePersistentNuGetConfigContentAsync(
        NuGetConfiguration configuration,
        FileInfo targetFile,
        ReadOnlyMemory<byte>? originalContent,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(targetFile);
        cancellationToken.ThrowIfCancellationRequested();

        var targetPath = PathNormalizer.ResolveToFilesystemPath(targetFile.FullName);
        var paths = configuration.Settings.ConfigPaths.ToList();
        var targetIndex = paths.FindIndex(path => string.Equals(
            PathNormalizer.ResolveToFilesystemPath(path), targetPath, StringComparisons.FileSystemPath));
        var inheritedPaths = targetIndex >= 0
            ? paths.Skip(targetIndex + 1).ToArray()
            : paths.Where(path => !BundleNuGetService.IsAncestorDirectory(
                targetFile.DirectoryName!, Path.GetDirectoryName(path)!)).ToArray();

        // NuGet's typed settings writer is file-backed. Persistence derives local edits
        // against the target's remaining hierarchy, rather than copying projection masks.
        string? conflictingSourceKey = null;
        using var file = await TemporaryNuGetConfigFile.CreateAsync(
            path => conflictingSourceKey = WriteConfig(
                configuration, targetFile, path, originalContent, globalPackagesFolder, inheritedPaths, cancellationToken)).ConfigureAwait(false);

        var proposedContent = await File.ReadAllBytesAsync(file.ConfigFile.FullName, cancellationToken).ConfigureAwait(false);
        // A discovery overlay can change without requiring any persisted configuration edit.
        var hasChanges = originalContent is not { } original ||
            !proposedContent.AsSpan().SequenceEqual(original.Span);
        if (hasChanges && conflictingSourceKey is not null)
        {
            throw new ProjectUpdaterException(string.Format(
                CultureInfo.CurrentCulture,
                UpdateCommandStrings.ChannelFeedAlreadyConfiguredFormat,
                targetFile.FullName,
                conflictingSourceKey));
        }

        return proposedContent;
    }

    private string? WriteConfig(
        NuGetConfiguration sourceConfiguration,
        FileInfo targetFile,
        string outputPath,
        ReadOnlyMemory<byte>? originalContent,
        string? globalPackagesFolder,
        IReadOnlyList<string> inheritedConfigPaths,
        CancellationToken cancellationToken)
    {
        var configuration = sourceConfiguration.AppHostConfig
            ?? throw new InvalidOperationException("The resolved configuration does not require a NuGet configuration change.");
        nuGetService.WriteNuGetConfig(
            configuration.SourceChanges with { GlobalPackagesFolder = globalPackagesFolder },
            outputPath, originalContent, omitRedundantDisabledSources: false, inheritedConfigPaths, cancellationToken);

        var outputFile = new FileInfo(outputPath);
        var settings = new Settings(outputFile.DirectoryName!, outputFile.Name, isMachineWide: false);
        var localSources = settings.GetSection(ConfigurationConstants.PackageSources)?.Items.OfType<SourceItem>().ToArray() ?? [];
        var localMappingItems = settings.GetSection(ConfigurationConstants.PackageSourceMapping)?.Items.ToArray() ?? [];
        ISettings inheritedSettings = inheritedConfigPaths.Count == 0
            ? NullSettings.Instance
            : Settings.LoadSettingsGivenConfigPaths(inheritedConfigPaths.ToList());
        var inheritedMappings = inheritedSettings.GetSection(ConfigurationConstants.PackageSourceMapping)?.Items
            .OfType<PackageSourceMappingSourceItem>().ToArray() ?? [];
        string? conflictingSourceKey = null;
        foreach (var source in sourceConfiguration.ConfigSources.Where(static source => source.IsAmbient))
        {
            if (!configuration.SelectedSourceKeys.Contains(source.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!source.IsEnabled)
            {
                conflictingSourceKey = source.Key;
                break;
            }
            if (!configuration.PackageSourceMappings.Any(mapping =>
                string.Equals(mapping.SourceKey, source.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var ambientSource = sourceConfiguration.Settings.Sources.Single(ambient =>
                string.Equals(ambient.Name, source.Key, StringComparison.OrdinalIgnoreCase));
            // Exact integration selections may extend local user mappings without taking
            // ownership of the alias or its existing patterns. NuGet replaces mapping entries
            // by key, so a local source definition alone cannot make inherited mappings safe
            // to extend.
            var canExtendIntegrationMapping = ambientSource.IsIntegrationSource &&
                (source.IsExplicitlyNamed ||
                 configuration.PackageSourceMappings
                     .Where(mapping => string.Equals(mapping.SourceKey, source.Key, StringComparison.OrdinalIgnoreCase))
                     .All(static mapping => mapping.Patterns.All(PackageSourceOverrideMappings.IsExactPackagePattern)));
            var inheritsMapping = !localMappingItems.Any(static item => item is ClearItem) &&
                !localMappingItems.OfType<PackageSourceMappingSourceItem>().Any(mapping =>
                    string.Equals(mapping.Key, source.Key, StringComparison.OrdinalIgnoreCase)) &&
                inheritedMappings.Any(mapping =>
                    string.Equals(mapping.Key, source.Key, StringComparison.OrdinalIgnoreCase));
            if (inheritsMapping ||
                !(NuGetConfigurationBuilder.IsCliManagedSourceKey(source.Key, configuration.WorkloadId) ||
                  canExtendIntegrationMapping) ||
                !localSources.Any(local =>
                    string.Equals(local.Key, source.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        NuGetSourceIdentity.Compute(
                            PackageSourceOverrideMappings.ResolveForWorkingDirectory(local.Value, targetFile.Directory!),
                            sourceConfiguration.Settings.SourceIdentityKey),
                        ambientSource.Identity,
                        StringComparison.Ordinal)))
            {
                conflictingSourceKey = source.Key;
                break;
            }
        }

        foreach (var removed in configuration.RemovedPackageSourceMappings)
        {
            if (!sourceConfiguration.Settings.Sources.Any(source =>
                source.IsIntegrationSource &&
                string.Equals(source.Name, removed.SourceKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var localPatterns = localMappingItems.OfType<PackageSourceMappingSourceItem>()
                .Where(mapping => string.Equals(mapping.Key, removed.SourceKey, StringComparison.OrdinalIgnoreCase))
                .SelectMany(static mapping => mapping.Patterns)
                .Select(static pattern => pattern.Pattern)
                .ToArray();
            var retainsLocalMapping = localPatterns.Any(pattern =>
                !removed.Patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase)) ||
                configuration.PackageSourceMappings.Any(mapping =>
                    string.Equals(mapping.SourceKey, removed.SourceKey, StringComparison.OrdinalIgnoreCase));
            // Deleting the last local entry would expose the parent's whole mapping entry,
            // potentially restoring the old exact route or changing unrelated eligibility.
            var exposesInheritedMapping = !retainsLocalMapping &&
                !localMappingItems.Any(static item => item is ClearItem) &&
                inheritedMappings.Any(mapping =>
                    string.Equals(mapping.Key, removed.SourceKey, StringComparison.OrdinalIgnoreCase));
            if (!localPatterns.Any(pattern => removed.Patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase)) ||
                exposesInheritedMapping)
            {
                throw new ProjectUpdaterException(string.Format(
                    CultureInfo.CurrentCulture,
                    UpdateCommandStrings.ChannelFeedAlreadyConfiguredFormat,
                    targetFile.FullName,
                    removed.SourceKey));
            }
        }

        var changedKeys = configuration.PackageSourceMappings.Select(static mapping => mapping.SourceKey)
            .Concat(configuration.RemovedPackageSourceMappings.Select(static mapping => mapping.SourceKey))
            .Concat(configuration.SourceChanges.RetiredSourceKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var key in changedKeys)
        {
            var existing = settings.GetSection(ConfigurationConstants.PackageSourceMapping)?.Items
                .OfType<PackageSourceMappingSourceItem>()
                .Where(mapping => string.Equals(mapping.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];
            var patterns = configuration.SourceChanges.RetiredSourceKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
                ? []
                : existing.SelectMany(static mapping => mapping.Patterns)
                    .Where(pattern => !configuration.RemovedPackageSourceMappings.Any(removed =>
                        string.Equals(removed.SourceKey, key, StringComparison.OrdinalIgnoreCase) &&
                        removed.Patterns.Contains(pattern.Pattern, StringComparer.OrdinalIgnoreCase)))
                    .Concat(configuration.PackageSourceMappings
                        .Where(mapping => string.Equals(mapping.SourceKey, key, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(static mapping => mapping.Patterns)
                        .Select(static pattern => new PackagePatternItem(pattern)))
                    .DistinctBy(static pattern => pattern.Pattern, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            if (existing.SelectMany(static mapping => mapping.Patterns).Select(static pattern => pattern.Pattern)
                .SequenceEqual(patterns.Select(static pattern => pattern.Pattern), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var mapping in existing)
            {
                settings.Remove(ConfigurationConstants.PackageSourceMapping, mapping);
            }
            if (patterns.Length > 0)
            {
                settings.AddOrUpdate(ConfigurationConstants.PackageSourceMapping,
                    new PackageSourceMappingSourceItem(key, patterns));
            }
        }

        // Removing local records may expose inherited policy. Retirement does not mask
        // or modify that policy, and its presence does not prevent local removal.
        settings.SaveToDisk();

        return conflictingSourceKey;
    }

    internal static Task ApplyAsync(
        DotNetAppHostNuGetConfigMergerCandidate candidate,
        CancellationToken cancellationToken)
        => ApplyAsync(candidate, static () => { }, cancellationToken);

    internal static async Task ApplyAsync(
        DotNetAppHostNuGetConfigMergerCandidate candidate,
        Action writeStarting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        candidate.TargetFile.Directory!.Create();

        // Check the baseline and write the approved snapshot under the same exclusive handle.
        // CreateNew also prevents overwriting a config created after preparation.
        await using var stream = candidate.TargetFile.Open(
            candidate.OriginalContent is null ? FileMode.CreateNew : FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        if (candidate.OriginalContent is { } originalContent)
        {
            using var currentContent = new MemoryStream();
            await stream.CopyToAsync(currentContent, cancellationToken);
            if (!currentContent.ToArray().AsSpan().SequenceEqual(originalContent.Span))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.UpdateCandidateFileChangedFormat, candidate.TargetFile.FullName));
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        writeStarting();
        stream.Position = 0;
        stream.SetLength(0);
        // Observe cancellation before committing, never after truncating the destination.
        await stream.WriteAsync(candidate.ProposedContent, CancellationToken.None);
    }

    internal static DirectoryInfo GetRecommendedNuGetConfigDirectory(IReadOnlyList<string> configPaths, DirectoryInfo appHostDirectory)
        => configPaths.Count > 0 && !IsGlobalNuGetConfig(configPaths[0])
            ? new DirectoryInfo(Path.GetDirectoryName(configPaths[0])!)
            : appHostDirectory;

    internal static bool TryFindNuGetConfigInDirectory(
        DirectoryInfo directory,
        [NotNullWhen(true)] out FileInfo? nugetConfigFile)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var matches = directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => string.Equals(file.Name, "nuget.config", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"Multiple NuGet.config files found in '{directory.FullName}' differing only by case.");
        }

        nugetConfigFile = matches.SingleOrDefault();
        return matches.Length == 1;
    }

    private static bool IsGlobalNuGetConfig(string path)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            return path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.Ordinal);
        }
        else
        {
            var globalNuGetFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget");
            return path.StartsWith(globalNuGetFolder, StringComparison.Ordinal);
        }
    }
}
