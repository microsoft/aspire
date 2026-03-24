// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aspire-remotehost-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
