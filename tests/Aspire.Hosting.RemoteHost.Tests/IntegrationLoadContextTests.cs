// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class IntegrationLoadContextTests
{
    [Fact]
    public void AspireTypeSystem_IsSharedFromDefaultContext()
    {
        var alc = new IntegrationLoadContext([AppContext.BaseDirectory], NullLogger.Instance);

        var sharedAssembly = alc.LoadFromAssemblyName(new AssemblyName("Aspire.TypeSystem"));

        Assert.Same(typeof(AtsContext).Assembly, sharedAssembly);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(sharedAssembly));
    }

    [Fact]
    public void VersionUnification_DefersToDefaultContext_WhenAlreadyLoaded()
    {
        // Aspire.Hosting is already in the default context (test project references it),
        // so version unification will defer to default rather than loading a second copy.
        var alc = new IntegrationLoadContext([AppContext.BaseDirectory], NullLogger.Instance);

        var assembly = alc.LoadFromAssemblyName(new AssemblyName("Aspire.Hosting"));
        Assert.NotNull(assembly);
        Assert.Same(typeof(IDistributedApplicationBuilder).Assembly, assembly);
    }

    [Fact]
    public void Load_UsesPackageProbeManifestForManagedAssemblies()
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Aspire.Hosting.CodeGeneration.Go.dll");
        Assert.True(File.Exists(assemblyPath));

        var manifestDirectory = Directory.CreateTempSubdirectory("aspire-remotehost-manifest-");
        try
        {
            var manifestPath = Path.Combine(manifestDirectory.FullName, "integration-package-probe-manifest.json");
            WriteProbeManifest(
                manifestPath,
                managedAssemblies:
                [
                    new { Name = "Aspire.Hosting.CodeGeneration.Go", Path = assemblyPath }
                ]);

            var probeManifest = IntegrationPackageProbeManifest.Load(manifestPath);
            var alc = new IntegrationLoadContext([], probeManifest, NullLogger.Instance);

            var assembly = alc.LoadFromAssemblyName(new AssemblyName("Aspire.Hosting.CodeGeneration.Go"));

            Assert.NotNull(assembly);
            Assert.Equal("Aspire.Hosting.CodeGeneration.Go", assembly.GetName().Name);
        }
        finally
        {
            Directory.Delete(manifestDirectory.FullName, recursive: true);
        }
    }

    [Fact]
    public void PackageProbeManifest_ResolvesNativeLibrariesByImportName()
    {
        var manifestDirectory = Directory.CreateTempSubdirectory("aspire-remotehost-native-");
        try
        {
            var nativeLibraryPath = Path.Combine(manifestDirectory.FullName, "runtimes", "linux-x64", "native", "libe_sqlite3.so");
            Directory.CreateDirectory(Path.GetDirectoryName(nativeLibraryPath)!);
            File.WriteAllText(nativeLibraryPath, string.Empty);

            var manifestPath = Path.Combine(manifestDirectory.FullName, "integration-package-probe-manifest.json");
            WriteProbeManifest(
                manifestPath,
                nativeLibraries:
                [
                    new { FileName = "libe_sqlite3.so", Path = nativeLibraryPath }
                ]);

            var probeManifest = IntegrationPackageProbeManifest.Load(manifestPath);

            Assert.Contains(nativeLibraryPath, probeManifest.GetNativeLibraryPaths("e_sqlite3"));
            Assert.Contains(nativeLibraryPath, probeManifest.GetNativeLibraryPaths("libe_sqlite3.so"));
        }
        finally
        {
            Directory.Delete(manifestDirectory.FullName, recursive: true);
        }
    }

    [Fact]
    public void PackageProbeManifest_ResolvesSatelliteResourceAssembliesByCulture()
    {
        var manifestDirectory = Directory.CreateTempSubdirectory("aspire-remotehost-resource-");
        try
        {
            var resourceAssemblyPath = Path.Combine(manifestDirectory.FullName, "fr", "Aspire.Hosting.Redis.resources.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(resourceAssemblyPath)!);
            File.WriteAllText(resourceAssemblyPath, string.Empty);

            var manifestPath = Path.Combine(manifestDirectory.FullName, "integration-package-probe-manifest.json");
            WriteProbeManifest(
                manifestPath,
                managedAssemblies:
                [
                    new { Name = "Aspire.Hosting.Redis.resources", Culture = "fr", Path = resourceAssemblyPath }
                ]);

            var probeManifest = IntegrationPackageProbeManifest.Load(manifestPath);

            Assert.Equal(
                resourceAssemblyPath,
                probeManifest.TryGetManagedAssemblyPath(new AssemblyName("Aspire.Hosting.Redis.resources")
                {
                    CultureName = "fr"
                }));
            Assert.Null(probeManifest.TryGetManagedAssemblyPath(new AssemblyName("Aspire.Hosting.Redis.resources")));
        }
        finally
        {
            Directory.Delete(manifestDirectory.FullName, recursive: true);
        }
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
}
