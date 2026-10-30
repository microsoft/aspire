// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using Aspire.Cli.DotNet;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

internal static class DotNetAppHostNuGetConfigTestHelper
{
    public static Task CreateOrUpdateAsync(
        DirectoryInfo directory,
        PackageChannel channel,
        Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback = null,
        CancellationToken cancellationToken = default)
    {
        var service = NuGetTestHelper.CreateService();
        var configuration = service.BuildChannelConfiguration(
            directory, "test", channel, packageSourceOverride: null, nugetServiceIndexOverride: null, cancellationToken);
        return new DotNetAppHostNuGetConfigMerger(service).CreateOrUpdateAsync(
            directory, configuration, createIfMissing: true,
            channel.ConfigureGlobalPackagesFolder ? CliPathHelper.StagingNuGetPackagesFolderName : null,
            confirmationCallback, cancellationToken);
    }

    public static Task CreateOrUpdateAsync(
        DirectoryInfo directory,
        PackageMapping[] mappings,
        bool configureGlobalPackagesFolder = false,
        Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback = null,
        CancellationToken cancellationToken = default)
    {
        var service = NuGetTestHelper.CreateService();
        return new DotNetAppHostNuGetConfigMerger(service).CreateOrUpdateAsync(
            directory, BuildConfiguration(service, directory, mappings), createIfMissing: true,
            configureGlobalPackagesFolder ? CliPathHelper.StagingNuGetPackagesFolderName : null,
            confirmationCallback, cancellationToken);
    }

    public static Task<DotNetAppHostNuGetConfigMergerCandidate?> PrepareAsync(
        DirectoryInfo directory, PackageChannel channel, bool createIfMissing, CancellationToken cancellationToken)
        => PrepareAsync(directory, channel.Mappings ?? [], createIfMissing, channel.ConfigureGlobalPackagesFolder, cancellationToken);

    public static Task<DotNetAppHostNuGetConfigMergerCandidate?> PrepareAsync(
        DirectoryInfo directory, PackageMapping[] mappings, bool createIfMissing, bool configureGlobalPackagesFolder, CancellationToken cancellationToken)
    {
        var service = NuGetTestHelper.CreateService();
        return new DotNetAppHostNuGetConfigMerger(service).PrepareAsync(
            directory, BuildConfiguration(service, directory, mappings), createIfMissing,
            configureGlobalPackagesFolder ? CliPathHelper.StagingNuGetPackagesFolderName : null,
            cancellationToken);
    }

    public static bool HasMissingSources(DirectoryInfo directory, PackageChannel channel)
    {
        if (channel.Mappings is not { Length: > 0 } mappings)
        {
            return false;
        }

        var configuration = BuildConfiguration(NuGetTestHelper.CreateService(), directory, mappings);
        return configuration.Overlay is { } overlay &&
            (overlay.Sources.Count > 0 || overlay.ClearDisabledPackageSources ||
             !configuration.Settings.PackageSourceMappings.Select(FormatMapping).Order()
                 .SequenceEqual(overlay.PackageSourceMappings.Select(FormatMapping).Order()));
    }

    private static string FormatMapping(NuGetPackageSourceMapping mapping)
        => $"{mapping.SourceKey.ToUpperInvariant()}:{string.Join(",", mapping.Patterns.Select(static pattern => pattern.ToUpperInvariant()).Order())}";

    private static NuGetConfiguration BuildConfiguration(BundleNuGetService service, DirectoryInfo directory, PackageMapping[] mappings)
        => service.BuildConfiguration(
            directory, "test", mappings.Length == 0 ? null : mappings,
            restrictToSelectedSources: false, hasAuthoritativeAspirePolicy: true,
            cancellationToken: TestContext.Current.CancellationToken);
}
