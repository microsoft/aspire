// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;

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
    public async Task SearchAsync_ReturnsResultsWhenOneSourceFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        CreatePackage(feedDirectory.FullName, "Aspire.Test.Package");

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var results = await client.SearchAsync(
            "Aspire.Test.Package",
            exactMatch: false,
            prerelease: false,
            take: 100,
            useCache: false,
            ["https://127.0.0.1:1/v3/index.json", feedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        var package = Assert.Single(results);
        Assert.Equal("Aspire.Test.Package", package.Id);
    }

    [Fact]
    public async Task SearchAsync_ThrowsRedactedErrorWhenAllSourcesFail()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(
            "Aspire.Test.Package",
            exactMatch: false,
            prerelease: false,
            take: 100,
            useCache: false,
            ["https://user:secret@127.0.0.1:1/v3/index.json?token=secret"],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken));

        Assert.Contains("https://***@127.0.0.1:1/v3/index.json", exception.Message);
        Assert.DoesNotContain("user", exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
        Assert.DoesNotContain("token", exception.Message);
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
            Assert.EndsWith(
                Path.Combine("runtimes", "win-x64", "lib", "net10.0", "Neutral.resources.dll"),
                manifest.TryGetManagedAssemblyPath(new("Neutral.resources")),
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(
                Path.Combine("runtimes", "win-x64", "lib", "net10.0", "fr", "RuntimeOnly.resources.dll"),
                manifest.TryGetManagedAssemblyPath(new("RuntimeOnly.resources, Culture=fr")),
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(
                Path.Combine("runtimes", "win-x64", "lib", "net10.0", "de", "RuntimeOnly.resources.dll"),
                manifest.TryGetManagedAssemblyPath(new("RuntimeOnly.resources, Culture=de")),
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
    public async Task RestoreAndWriteManifestAsync_UsesRuntimeGraphFallbackAssets()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(
            feedDirectory.FullName,
            packageId,
            additionalEntries: new Dictionary<string, string>
            {
                ["runtimes/unix-x64/lib/net10.0/UnixFallback.dll"] = "unix-fallback"
            });

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);
        string? packageRoot = null;
        try
        {
            var restoredPackages = await client.RestoreAsync(
                [(packageId, "1.0.0")],
                "net10.0",
                "linux-x64",
                restoreDirectory.FullName,
                [feedDirectory.FullName],
                nugetConfigPath: null,
                workspace.WorkspaceRoot.FullName,
                TestContext.Current.CancellationToken);
            packageRoot = Path.GetDirectoryName(restoredPackages[0].InstallPath);
            var manifestPath = Path.Combine(restoreDirectory.FullName, IntegrationPackageProbeManifest.FileName);
            await client.WriteManifestAsync(
                restoredPackages,
                manifestPath,
                "net10.0",
                "linux-x64",
                TestContext.Current.CancellationToken);

            var manifest = IntegrationPackageProbeManifest.Load(manifestPath);
            Assert.EndsWith(
                Path.Combine("runtimes", "unix-x64", "lib", "net10.0", "UnixFallback.dll"),
                manifest.TryGetManagedAssemblyPath(new("UnixFallback")),
                StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task RestoreAsync_IgnoresMissingDependencyFromUnselectedCandidate()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageA = $"Aspire.Test.Package.A.{Guid.NewGuid():N}";
        var packageB = $"Aspire.Test.Package.B.{Guid.NewGuid():N}";
        var missingPackage = $"Aspire.Test.Package.Missing.{Guid.NewGuid():N}";
        CreatePackage(
            feedDirectory.FullName,
            packageA,
            version: "1.0.0",
            dependencies: [(missingPackage, "1.0.0")]);
        CreatePackage(feedDirectory.FullName, packageA, version: "2.0.0");
        CreatePackage(
            feedDirectory.FullName,
            packageB,
            dependencies: [(packageA, "[2.0.0]")]);

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var restoredPackages = await client.RestoreAsync(
            [(packageA, "[1.0.0,)"), (packageB, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [feedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        Assert.Contains(restoredPackages, package => package.Id == packageA && package.Version == "2.0.0");
        Assert.Contains(restoredPackages, package => package.Id == packageB && package.Version == "1.0.0");
        Assert.DoesNotContain(restoredPackages, package => package.Id == missingPackage);
    }

    [Fact]
    public async Task RestoreAsync_UsesExplicitSourceWhenMappingRefersToUnavailableSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configDirectory = workspace.CreateDirectory("config");
        var explicitFeed = workspace.CreateDirectory("explicit-feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(explicitFeed.FullName, packageId, "explicit-source");

        var nugetConfigPath = Path.Combine(configDirectory.FullName, "nuget.config");
        File.WriteAllText(
            nugetConfigPath,
            $"""
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packagesDirectory.FullName}" />
              </config>
              <packageSources>
                <clear />
              </packageSources>
              <packageSourceMapping>
                <packageSource key=".">
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
            [explicitFeed.FullName],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        var restoredPackage = Assert.Single(restoredPackages);
        Assert.Equal(
            "explicit-source",
            await File.ReadAllTextAsync(
                Path.Combine(restoredPackage.InstallPath, "lib", "net10.0", "Aspire.Test.Package.dll"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreAsync_PrefersAvailableMappedSourceOverExplicitSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var mappedFeed = workspace.CreateDirectory("mapped-feed");
        var explicitFeed = workspace.CreateDirectory("explicit-feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(mappedFeed.FullName, packageId, "mapped-source");
        CreatePackage(explicitFeed.FullName, packageId, "explicit-source");

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
                <add key="mapped" value="{mappedFeed.FullName}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key=".">
                  <package pattern="Aspire.Test.Package.*" />
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
            [explicitFeed.FullName],
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

    [Fact]
    public async Task RestoreAsync_RejectsExplicitSourceWhenPackageHasNoMapping()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var explicitFeed = workspace.CreateDirectory("explicit-feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(explicitFeed.FullName, packageId);

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
              </packageSources>
              <packageSourceMapping>
                <packageSource key=".">
                  <package pattern="Other.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RestoreAsync(
            [(packageId, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [explicitFeed.FullName],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken));

        Assert.Equal(
            $"NuGet package source mapping has no matching source for package '{packageId}'.",
            exception.Message);
    }

    [Fact]
    public async Task RestoreAsync_RedactsUnavailableMappedSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        const string sensitiveSource = "https://user:secret@example.com/v3/index.json?token=secret";

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
              </packageSources>
              <packageSourceMapping>
                <packageSource key="{sensitiveSource}">
                  <package pattern="Aspire.Test.Package.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RestoreAsync(
            [(packageId, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken));

        Assert.Contains("example.com", exception.Message);
        Assert.DoesNotContain("user", exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
        Assert.DoesNotContain("token", exception.Message);
    }

    [Fact]
    public async Task RestoreAsync_ReplacesIncompleteGlobalPackage()
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
        var settings = Settings.LoadSpecificSettings(
            Path.GetDirectoryName(nugetConfigPath)!,
            Path.GetFileName(nugetConfigPath));
        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
        var incompleteInstallPath = Path.Combine(globalPackagesFolder, packageId.ToLowerInvariant(), "1.0.0");
        Directory.CreateDirectory(incompleteInstallPath);
        File.WriteAllText(Path.Combine(incompleteInstallPath, "partial.txt"), "incomplete");

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        try
        {
            var package = Assert.Single(await client.RestoreAsync(
                [(packageId, "1.0.0")],
                "net10.0",
                runtimeIdentifier: null,
                restoreDirectory.FullName,
                [],
                nugetConfigPath,
                workspace.WorkspaceRoot.FullName,
                TestContext.Current.CancellationToken));

            Assert.Equal(incompleteInstallPath, package.InstallPath, ignoreCase: true);
            Assert.True(File.Exists(Path.Combine(package.InstallPath, ".nupkg.metadata")));
            Assert.True(File.Exists(Path.Combine(package.InstallPath, "lib", "net10.0", "Aspire.Test.Package.dll")));
        }
        finally
        {
            Directory.Delete(Path.Combine(globalPackagesFolder, packageId.ToLowerInvariant()), recursive: true);
        }
    }

    private static void CreatePackage(
        string feedDirectory,
        string packageId,
        string baseAssemblyContents = "base",
        string version = "1.0.0",
        IReadOnlyDictionary<string, string>? additionalEntries = null,
        IReadOnlyList<(string Id, string Version)>? dependencies = null)
    {
        var packagePath = Path.Combine(feedDirectory, $"{packageId}.{version}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var dependencyElements = dependencies is null
            ? string.Empty
            : $"""
                  <dependencies>
                    <group targetFramework="net10.0">
                {string.Join(
                    Environment.NewLine,
                    dependencies.Select(dependency => $"      <dependency id=\"{dependency.Id}\" version=\"{dependency.Version}\" />"))}
                    </group>
                  </dependencies>
                """;
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
            {dependencyElements}
              </metadata>
            </package>
            """);
        WriteEntry(archive, "lib/net10.0/Aspire.Test.Package.dll", baseAssemblyContents);
        WriteEntry(archive, "runtimes/win-x64/lib/net10.0/Aspire.Test.Package.dll", "runtime");
        WriteEntry(archive, "runtimes/win-x64/lib/net10.0/RuntimeOnly.dll", "runtime-only");
        WriteEntry(archive, "runtimes/win-x64/lib/net10.0/Neutral.resources.dll", "neutral");
        WriteEntry(archive, "runtimes/win-x64/lib/net10.0/fr/RuntimeOnly.resources.dll", "french");
        WriteEntry(archive, "runtimes/win-x64/lib/net10.0/de/RuntimeOnly.resources.dll", "german");
        WriteEntry(archive, "runtimes/win-x64/native/native-test.dll", "native");

        if (additionalEntries is not null)
        {
            foreach (var (path, contents) in additionalEntries)
            {
                WriteEntry(archive, path, contents);
            }
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(contents);
    }
}
