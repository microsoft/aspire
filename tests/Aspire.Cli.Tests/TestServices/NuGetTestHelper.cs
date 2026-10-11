// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using global::NuGet.Configuration;

namespace Aspire.Cli.Tests.TestServices;

internal static class NuGetTestHelper
{
    private static readonly IMachineWideSettings s_emptyMachineWideSettings = new TestMachineWideSettings(NullSettings.Instance);

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

    public static BundleNuGetService CreateService(IMachineWideSettings? machineWideSettings = null)
        => new(NullLogger<BundleNuGetService>.Instance, CreateClient(machineWideSettings));

    public static NuGetClient CreateClient(IMachineWideSettings? machineWideSettings = null)
        => new(new TestFeatures(), new TestEnvironment(), NullLogger<NuGetClient>.Instance)
        {
            AmbientSettingsLoader = LoadSettings,
            MachineWideSettingsFactory = () => machineWideSettings ?? s_emptyMachineWideSettings
        };

    public static ISettings LoadSettings(string workingDirectory, IMachineWideSettings? machineWideSettings = null)
    {
        // Preserve directory-level inheritance and NuGet's filename precedence, but supply
        // test-owned user/machine defaults without changing environment variables in parallel tests.
        var configPaths = new List<string>();
        for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
        {
            var configPath = Settings.OrderedSettingsFileNames
                .Select(name => Path.Combine(directory.FullName, name))
                .FirstOrDefault(File.Exists);
            if (configPath is not null)
            {
                configPaths.AddRange(Settings.LoadSpecificSettings(directory.FullName, Path.GetFileName(configPath)).GetConfigFilePaths());
            }
        }

        configPaths.Add(Path.Combine(AppContext.BaseDirectory, "NuGetTestSettings.config"));
        configPaths.AddRange((machineWideSettings ?? s_emptyMachineWideSettings).Settings.GetConfigFilePaths());

        return Settings.LoadSettingsGivenConfigPaths(configPaths);
    }

    public static string[] GetEligiblePackageSources(string workingDirectory, string packageId, IMachineWideSettings? machineWideSettings = null)
    {
        var settings = LoadSettings(workingDirectory, machineWideSettings);
        var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        var keys = mapping.GetConfiguredPackageSources(packageId);
        return new PackageSourceProvider(settings).LoadPackageSources()
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
