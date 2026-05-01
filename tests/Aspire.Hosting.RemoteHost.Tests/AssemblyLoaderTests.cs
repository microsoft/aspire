// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class AssemblyLoaderTests
{
    [Fact]
    public void DiscoverAspireHostingAssemblies_FindsAssembliesInProbeDirectories()
    {
        using var integrationLibs = new TemporaryDirectory();
        using var applicationBase = new TemporaryDirectory();

        integrationLibs.CreateFile("Aspire.Hosting.Redis.dll");
        integrationLibs.CreateFile("Aspire.Hosting.Azure.ApplicationInsights.dll");
        integrationLibs.CreateFile("NotAspire.dll");
        applicationBase.CreateFile("Aspire.Hosting.Azure.AppService.dll");
        applicationBase.CreateFile("Aspire.Hosting.AppHost.dll");
        applicationBase.CreateFile("Aspire.AppHost.Sdk.dll");

        var assemblyNames = AssemblyLoader.DiscoverAspireHostingAssemblies(
            [integrationLibs.Path, applicationBase.Path, Path.Combine(applicationBase.Path, "missing")]);

        Assert.Equal(
            [
                "Aspire.Hosting.Azure.ApplicationInsights",
                "Aspire.Hosting.Azure.AppService",
                "Aspire.Hosting.Redis"
            ],
            assemblyNames);
    }

    [Fact]
    public void GetAssemblyNamesToLoad_PreservesConfiguredAssembliesAndAddsTransitives()
    {
        using var integrationLibs = new TemporaryDirectory();
        using var applicationBase = new TemporaryDirectory();

        integrationLibs.CreateFile("Aspire.Hosting.Azure.ApplicationInsights.dll");
        integrationLibs.CreateFile("Aspire.Hosting.Azure.OperationalInsights.dll");
        integrationLibs.CreateFile("Aspire.Hosting.Azure.AppService.dll");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AtsAssemblies:0"] = "Aspire.Hosting",
                ["AtsAssemblies:1"] = "My.Custom.Integration",
                ["AtsAssemblies:2"] = "Aspire.Hosting.Azure.AppService",
            })
            .Build();

        var assemblyNames = AssemblyLoader.GetAssemblyNamesToLoad(
            configuration,
            integrationLibs.Path,
            applicationBase.Path);

        Assert.Equal(
            [
                "Aspire.Hosting",
                "My.Custom.Integration",
                "Aspire.Hosting.Azure.AppService",
                "Aspire.Hosting.Azure.ApplicationInsights",
                "Aspire.Hosting.Azure.OperationalInsights"
            ],
            assemblyNames);
    }

    [Fact]
    public void GetAssemblyNamesToLoad_AddsAutoDiscoveredAssembliesFromPackageProbeManifest()
    {
        using var manifestDirectory = new TemporaryDirectory();
        using var packageAssemblyDirectory = new TemporaryDirectory();

        var runtimeAssemblyPath = System.IO.Path.Combine(packageAssemblyDirectory.Path, "Aspire.Hosting.Azure.OperationalInsights.dll");
        var resourceAssemblyPath = System.IO.Path.Combine(packageAssemblyDirectory.Path, "de", "Aspire.Hosting.resources.dll");
        File.WriteAllText(runtimeAssemblyPath, string.Empty);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(resourceAssemblyPath)!);
        File.WriteAllText(resourceAssemblyPath, string.Empty);

        var manifestPath = System.IO.Path.Combine(manifestDirectory.Path, "integration-package-probe-manifest.json");
        WriteProbeManifest(
            manifestPath,
            managedAssemblies:
            [
                new { Name = "Aspire.Hosting.Azure.OperationalInsights", Path = runtimeAssemblyPath },
                new { Name = "Aspire.Hosting.resources", Culture = "de", Path = resourceAssemblyPath }
            ]);

        var probeManifest = IntegrationPackageProbeManifest.Load(manifestPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AtsAssemblies:0"] = "Aspire.Hosting.Azure.AppService"
            })
            .Build();

        var assemblyNames = AssemblyLoader.GetAssemblyNamesToLoad(
            configuration,
            integrationLibsPath: null,
            applicationBasePath: System.IO.Path.Combine(manifestDirectory.Path, "missing"),
            packageProbeManifest: probeManifest);

        Assert.Equal(
        [
            "Aspire.Hosting.Azure.AppService",
            "Aspire.Hosting.Azure.OperationalInsights"
        ],
        assemblyNames);
    }

    [Fact]
    public void GetAssemblyNamesToLoad_CombinesPackageProbeManifestAndProjectLibs()
    {
        using var integrationLibs = new TemporaryDirectory();
        using var manifestDirectory = new TemporaryDirectory();
        using var packageAssemblyDirectory = new TemporaryDirectory();

        integrationLibs.CreateFile("Aspire.Hosting.ProjectIntegration.dll");

        var packageAssemblyPath = System.IO.Path.Combine(packageAssemblyDirectory.Path, "Aspire.Hosting.PackageIntegration.dll");
        File.WriteAllText(packageAssemblyPath, string.Empty);

        var manifestPath = System.IO.Path.Combine(manifestDirectory.Path, "integration-package-probe-manifest.json");
        WriteProbeManifest(
            manifestPath,
            managedAssemblies:
            [
                new { Name = "Aspire.Hosting.PackageIntegration", Path = packageAssemblyPath }
            ]);

        var probeManifest = IntegrationPackageProbeManifest.Load(manifestPath);
        var configuration = new ConfigurationBuilder().Build();

        var assemblyNames = AssemblyLoader.GetAssemblyNamesToLoad(
            configuration,
            integrationLibs.Path,
            applicationBasePath: System.IO.Path.Combine(manifestDirectory.Path, "missing"),
            packageProbeManifest: probeManifest);

        Assert.Equal(
        [
            "Aspire.Hosting.PackageIntegration",
            "Aspire.Hosting.ProjectIntegration"
        ],
        assemblyNames);
    }

    [Fact]
    public void GetAssemblies_LoadsConfiguredAssembly()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AtsAssemblies:0"] = "Aspire.Hosting"
            })
            .Build();

        var loader = new AssemblyLoader(configuration, NullLogger<AssemblyLoader>.Instance);

        var assemblies = loader.GetAssemblies();
        Assert.Contains(assemblies, a => string.Equals(a.GetName().Name, "Aspire.Hosting", StringComparison.Ordinal));
    }

    private static void WriteProbeManifest(string manifestPath, IEnumerable<object>? managedAssemblies = null, IEnumerable<object>? nativeLibraries = null)
    {
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    ManagedAssemblies = managedAssemblies ?? [],
                    NativeLibraries = nativeLibraries ?? []
                },
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory;

        public TemporaryDirectory()
        {
            _directory = Directory.CreateTempSubdirectory("aspire-remotehost-");
        }

        public string Path => _directory.FullName;

        public void CreateFile(string fileName)
        {
            File.WriteAllText(System.IO.Path.Combine(Path, fileName), string.Empty);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
