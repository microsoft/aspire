// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetClientTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task SearchAsync_ReturnsResultsFromAllPages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        CreatePackage(feedDirectory.FullName, "Aspire.Test.Package.One");
        CreatePackage(feedDirectory.FullName, "Aspire.Test.Package.Two");

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var results = await client.SearchAsync(
            "Aspire.Test.Package",
            exactMatch: false,
            prerelease: false,
            take: 1,
            useCache: false,
            [feedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            results,
            package => Assert.Equal("Aspire.Test.Package.One", package.Id),
            package => Assert.Equal("Aspire.Test.Package.Two", package.Id));
    }

    [Fact]
    public async Task SearchAsync_ExactMatchReturnsAllVersions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstFeedDirectory = workspace.CreateDirectory("first-feed");
        var secondFeedDirectory = workspace.CreateDirectory("second-feed");
        CreatePackage(firstFeedDirectory.FullName, "Aspire.Test.Package");
        CreatePackage(secondFeedDirectory.FullName, "Aspire.Test.Package", version: "2.0.0");

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var results = await client.SearchAsync(
            "Aspire.Test.Package",
            exactMatch: true,
            prerelease: false,
            take: 1,
            useCache: false,
            [firstFeedDirectory.FullName, secondFeedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            results,
            package =>
            {
                Assert.Equal("Aspire.Test.Package", package.Id);
                Assert.Equal("2.0.0", package.Version);
                Assert.Equal(secondFeedDirectory.FullName, package.Source);
                Assert.Equal(["2.0.0"], package.AllVersions);
            },
            package =>
            {
                Assert.Equal("Aspire.Test.Package", package.Id);
                Assert.Equal("1.0.0", package.Version);
                Assert.Equal(firstFeedDirectory.FullName, package.Source);
                Assert.Equal(["1.0.0"], package.AllVersions);
            });
    }

    [Fact]
    public async Task RestoreAndWriteManifestAsync_UsesLocalPackageRuntimeAssets()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(feedDirectory.FullName, packageId);

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        File.WriteAllText(
            nugetConfigPath,
            $"""
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packagesDirectory.FullName}" />
              </config>
              <packageSources>
                <clear />
                <add key="local" value="{feedDirectory.FullName}" />
              </packageSources>
            </configuration>
            """);
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        string? packageRoot = null;
        try
        {
            var restoredPackages = await client.RestoreAsync(
                [(packageId, "[1.0.0]")],
                "net10.0",
                "win-x64",
                restoreDirectory.FullName,
                [],
                nugetConfigPath,
                workspace.WorkspaceRoot.FullName,
                TestContext.Current.CancellationToken);
            packageRoot = Path.GetDirectoryName(restoredPackages[0].InstallPath);
            var manifestPath = Path.Combine(restoreDirectory.FullName, IntegrationPackageProbeManifest.FileName);
            await client.WriteManifestAsync(
                restoredPackages,
                manifestPath,
                "net10.0",
                "win-x64",
                TestContext.Current.CancellationToken);

            var manifest = IntegrationPackageProbeManifest.Load(manifestPath);
            Assert.Equal(
                Path.Combine(
                    restoredPackages[0].InstallPath,
                    "runtimes",
                    "win-x64",
                    "lib",
                    "net10.0",
                    "Aspire.Test.Package.dll"),
                manifest.TryGetManagedAssemblyPath(new("Aspire.Test.Package")));
            Assert.EndsWith(
                Path.Combine("runtimes", "win-x64", "lib", "net10.0", "RuntimeOnly.dll"),
                manifest.TryGetManagedAssemblyPath(new("RuntimeOnly")),
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(manifest.GetNativeLibraryPaths("native-test"));
        }
        finally
        {
            if (packageRoot is not null)
            {
                Directory.Delete(packageRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestoreAsync_HonorsPackageSourceMapping()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstFeed = workspace.CreateDirectory("first-feed");
        var mappedFeed = workspace.CreateDirectory("mapped-feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(firstFeed.FullName, packageId, "wrong-source");
        CreatePackage(mappedFeed.FullName, packageId, "mapped-source");

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        File.WriteAllText(
            nugetConfigPath,
            $"""
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packagesDirectory.FullName}" />
              </config>
              <packageSources>
                <clear />
                <add key="first" value="{firstFeed.FullName}" />
                <add key="mapped" value="{mappedFeed.FullName}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="first">
                  <package pattern="Other.*" />
                </packageSource>
                <packageSource key="mapped">
                  <package pattern="Aspire.Test.Package.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var restoredPackages = await client.RestoreAsync(
            [(packageId, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        var restoredPackage = Assert.Single(restoredPackages);
        Assert.Equal(
            "mapped-source",
            await File.ReadAllTextAsync(
                Path.Combine(restoredPackage.InstallPath, "lib", "net10.0", "Aspire.Test.Package.dll"),
                TestContext.Current.CancellationToken));
    }

    private static void CreatePackage(
        string feedDirectory,
        string packageId,
        string baseAssemblyContents = "base",
        string version = "1.0.0")
    {
        var packagePath = Path.Combine(feedDirectory, $"{packageId}.{version}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            $"{packageId}.nuspec",
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{packageId}</id>
                <version>{version}</version>
                <authors>Aspire</authors>
                <description>Package used to validate in-process NuGet restore.</description>
              </metadata>
            </package>
            """);
        WriteEntry(archive, "lib/net10.0/Aspire.Test.Package.dll", baseAssemblyContents);
        WriteEntry(archive, "runtimes/win-x64/lib/net10.0/Aspire.Test.Package.dll", "runtime");
        WriteEntry(archive, "runtimes/win-x64/lib/net10.0/RuntimeOnly.dll", "runtime-only");
        WriteEntry(archive, "runtimes/win-x64/native/native-test.dll", "native");
    }

    private static void WriteEntry(ZipArchive archive, string path, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(contents);
    }
}
