// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.TestServices;

internal static class NuGetTestHelper
{
    internal static void CreatePackage(
        DirectoryInfo feed, string id, string version, IReadOnlyDictionary<string, string> files)
    {
        using var archive = ZipFile.Open(Path.Combine(feed.FullName, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
        Write($"{id}.nuspec", $"""
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id><version>{version}</version><authors>Aspire</authors>
                <description>Native restore fixture.</description>
              </metadata>
            </package>
            """);
        foreach (var (path, content) in files)
        {
            Write(path, content);
        }

        void Write(string path, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open());
            writer.Write(content);
        }
    }

    public static BundleNuGetService CreateService()
        => new(NullLogger<BundleNuGetService>.Instance, CreateClient());

    public static NuGetClient CreateClient()
        => new(new TestFeatures(), new TestEnvironment(), NullLogger<NuGetClient>.Instance);

    public static string[] GetEligiblePackageSources(string workingDirectory, string packageId)
    {
        var settings = global::NuGet.Configuration.Settings.LoadDefaultSettings(workingDirectory);
        var mapping = global::NuGet.Configuration.PackageSourceMapping.GetPackageSourceMapping(settings);
        var keys = mapping.GetConfiguredPackageSources(packageId);
        return new global::NuGet.Configuration.PackageSourceProvider(settings).LoadPackageSources()
            .Where(source => source.IsEnabled && (!mapping.IsEnabled || keys.Contains(source.Name, StringComparer.OrdinalIgnoreCase)))
            .Select(static source => source.Source)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static Task<TemporaryNuGetConfigFile> CreateStandaloneConfigurationAsync(
        PackageMapping[] mappings,
        bool configureGlobalPackagesFolder = false,
        string? globalPackagesFolderValue = null)
    {
        var settings = new NuGetSettingsInfo([], "standalone", [], [], [], [], [], new byte[NuGetSourceIdentity.KeySizeInBytes]);
        var configuration = NuGetConfigurationBuilder.Build(
            settings, "standalone", mappings, restrictToSelectedSources: false,
            packageScopedAppendSource: null, hasAuthoritativeAspirePolicy: true);
        return TemporaryNuGetConfigFile.CreateAsync(path => CreateService().WriteNuGetConfig(
            configuration, path, configureGlobalPackagesFolder ? globalPackagesFolderValue ?? ".nugetpackages" : null,
            TestContext.Current.CancellationToken));
    }
}
