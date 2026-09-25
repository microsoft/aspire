// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NuGet.Configuration;
using NuGet.ProjectModel;
using NuGet.Packaging;
using NuGet.Protocol;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetClientTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task SearchAsync_ReturnsOnlyTheFirstPage()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        CreatePackage(feedDirectory.FullName, "Aspire.Test.Package.One");
        CreatePackage(feedDirectory.FullName, "Aspire.Test.Package.Two");

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        // The aspire-managed helper requested one page per source and never paged further.
        var results = await client.SearchAsync(
            "Aspire.Test.Package",
            prerelease: false,
            take: 1,
            [feedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAsync_KeepsOneEntryPerPackageAcrossSources()
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
            prerelease: false,
            take: 1000,
            [firstFeedDirectory.FullName, secondFeedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        // Only the winning source's entry survives, so its versions are not merged with the other source's.
        var package = Assert.Single(results);
        Assert.Equal("Aspire.Test.Package", package.Id);
        Assert.Equal("2.0.0", package.Version);
        Assert.Equal(secondFeedDirectory.FullName, package.Source);
        Assert.Equal(["2.0.0"], package.AllVersions);
    }

    [Fact]
    public async Task SearchAsync_ComparesVersionsAsStringsAcrossSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstFeedDirectory = workspace.CreateDirectory("first-feed");
        var secondFeedDirectory = workspace.CreateDirectory("second-feed");
        CreatePackage(firstFeedDirectory.FullName, "Aspire.Test.Package", version: "9.0.0");
        CreatePackage(secondFeedDirectory.FullName, "Aspire.Test.Package", version: "10.0.0");

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var results = await client.SearchAsync(
            "Aspire.Test.Package",
            prerelease: false,
            take: 1000,
            [firstFeedDirectory.FullName, secondFeedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        // The aspire-managed helper picked the entry with the highest version *string*, so "9.0.0" wins over
        // "10.0.0". This pins that behavior so a change to it is a deliberate decision rather than a side effect.
        var package = Assert.Single(results);
        Assert.Equal("9.0.0", package.Version);
        Assert.Equal(firstFeedDirectory.FullName, package.Source);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToDiscoveryWhenConfigFileIsMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        CreatePackage(feedDirectory.FullName, "Aspire.Test.Package");
        File.WriteAllText(
            Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config"),
            $"""
            <configuration>
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

        var results = await client.SearchAsync(
            "Aspire.Test.Package",
            prerelease: false,
            take: 1000,
            [],
            nugetConfigPath: Path.Combine(workspace.WorkspaceRoot.FullName, "missing", "nuget.config"),
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        var package = Assert.Single(results);
        Assert.Equal("Aspire.Test.Package", package.Id);
        Assert.Equal(feedDirectory.FullName, package.Source);
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
            prerelease: false,
            take: 100,
            ["https://127.0.0.1:1/v3/index.json", feedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        var package = Assert.Single(results);
        Assert.Equal("Aspire.Test.Package", package.Id);
    }

    [Fact]
    public async Task RestoreAsync_FailureReportsHelperOutput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Missing.{Guid.NewGuid():N}";
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

        var exception = await Assert.ThrowsAsync<NuGetOperationException>(() => client.RestoreAsync(
            [(packageId, "[1.0.0]")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken));

        // Without debug logging the helper wrote only NuGet's warnings and errors, prefixed the way its logger
        // prefixed them, followed by its own summary of the failed restore.
        var lines = exception.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.StartsWith("ERROR: ", StringComparison.Ordinal) && line.Contains(packageId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.StartsWith("Error: Restore failed: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreAsync_UnexpectedFailureDoesNotRetainOriginalException()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var restoreDirectory = workspace.CreateDirectory("restore");
        const string invalidVersion = "not-a-version";
        var logger = new FakeLogger<NuGetClient>();
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            logger);

        var exception = await Assert.ThrowsAsync<NuGetOperationException>(() => client.RestoreAsync(
            [("Aspire.Test.Package", invalidVersion)],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            sources: [],
            nugetConfigPaths: [],
            workspace.WorkspaceRoot.FullName,
            globalPackagesFolderOverride: null,
            sensitiveSources: [],
            TestContext.Current.CancellationToken));

        Assert.Null(exception.InnerException);
        Assert.Contains(invalidVersion, exception.Output, StringComparison.Ordinal);
        Assert.Contains(
            logger.Collector.GetSnapshot(),
            record => record.Message.Contains(invalidVersion, StringComparison.Ordinal));
    }

    // The tests below observe process-wide state -- the real environment and NuGet's static credential service -- so
    // each runs in its own process, where no other test's operation can overlap it.

    [Fact]
    public void RestoreAsync_RestoresSignatureVerificationVariableAfterSuccess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(feedDirectory.FullName, packageId);
        var nugetConfigPath = CreateLocalFeedConfig(workspace, feedDirectory, workspace.CreateDirectory("packages"));

        RemoteExecutor.Invoke(
            static async (packageId, configPath, restorePath, workingDirectory) =>
            {
                Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, null);
                var client = new NuGetClient(new TestFeatures(), TestEnvironment.CreateLinux(), NullLogger<NuGetClient>.Instance);

                await client.RestoreAsync(
                    [(packageId, "[1.0.0]")],
                    "net10.0",
                    runtimeIdentifier: null,
                    restorePath,
                    [],
                    configPath,
                    workingDirectory,
                    CancellationToken.None);

                Assert.Null(Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
            },
            packageId,
            nugetConfigPath,
            workspace.CreateDirectory("restore").FullName,
            workspace.WorkspaceRoot.FullName).Dispose();
    }

    [Fact]
    public void RestoreAsync_RestoresSignatureVerificationVariableAfterFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var nugetConfigPath = CreateLocalFeedConfig(workspace, workspace.CreateDirectory("feed"), workspace.CreateDirectory("packages"));

        RemoteExecutor.Invoke(
            static async (configPath, restorePath, workingDirectory) =>
            {
                Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, null);
                var client = new NuGetClient(new TestFeatures(), TestEnvironment.CreateLinux(), NullLogger<NuGetClient>.Instance);

                await Assert.ThrowsAsync<NuGetOperationException>(() => client.RestoreAsync(
                    [($"Aspire.Test.Missing.{Guid.NewGuid():N}", "[1.0.0]")],
                    "net10.0",
                    runtimeIdentifier: null,
                    restorePath,
                    [],
                    configPath,
                    workingDirectory,
                    CancellationToken.None));

                Assert.Null(Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
            },
            nugetConfigPath,
            workspace.CreateDirectory("restore").FullName,
            workspace.WorkspaceRoot.FullName).Dispose();
    }

    [Fact]
    public void RestoreAsync_RestoresSignatureVerificationVariableAfterCancellation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(feedDirectory.FullName, packageId);
        var nugetConfigPath = CreateLocalFeedConfig(workspace, feedDirectory, workspace.CreateDirectory("packages"));

        RemoteExecutor.Invoke(
            static async (packageId, configPath, restorePath, workingDirectory) =>
            {
                Environment.SetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification, null);
                var client = new NuGetClient(new TestFeatures(), TestEnvironment.CreateLinux(), NullLogger<NuGetClient>.Instance);
                using var cancellationSource = new CancellationTokenSource();
                cancellationSource.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RestoreAsync(
                    [(packageId, "[1.0.0]")],
                    "net10.0",
                    runtimeIdentifier: null,
                    restorePath,
                    [],
                    configPath,
                    workingDirectory,
                    cancellationSource.Token));

                Assert.Null(Environment.GetEnvironmentVariable(NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification));
            },
            packageId,
            nugetConfigPath,
            workspace.CreateDirectory("restore").FullName,
            workspace.WorkspaceRoot.FullName).Dispose();
    }

    [Fact]
    public void BeginOperation_ResetsNuGetStateWhenLastOverlappingOperationEnds()
    {
        RemoteExecutor.Invoke(static () =>
        {
            var client = new NuGetClient(new TestFeatures(), new TestEnvironment(), NullLogger<NuGetClient>.Instance);

            var first = client.BeginOperation();
            var second = client.BeginOperation();
            Assert.NotNull(HttpHandlerResourceV3.CredentialService);

            // Ending one of two overlapping operations, even twice, must not reset state the other is still using.
            first.Dispose();
            first.Dispose();
            Assert.NotNull(HttpHandlerResourceV3.CredentialService);

            second.Dispose();
            Assert.Null(HttpHandlerResourceV3.CredentialService);

            // The next operation sets the credential service up again after the reset.
            using (client.BeginOperation())
            {
                Assert.NotNull(HttpHandlerResourceV3.CredentialService);
            }

            Assert.Null(HttpHandlerResourceV3.CredentialService);
        }).Dispose();
    }

    [Fact]
    public void DiagnosticNuGetLogger_RedactsUnionOfActiveSensitiveSources()
    {
        const string firstSource = "https://first.example/v3/index.json?sig=first-secret";
        const string secondSource = "https://second.example/v3/index.json?sig=second-secret";
        var logger = new FakeLogger<NuGetClient>();
        var diagnosticLogger = new NuGetClient.DiagnosticNuGetLogger(logger);

        using var first = diagnosticLogger.RegisterSensitiveSources([firstSource]);
        using var second = diagnosticLogger.RegisterSensitiveSources([secondSource]);

        diagnosticLogger.LogDebug(
            "Credential provider checked https://first-cdn.example/v3-flatcontainer/package/index.json?sig=first-secret " +
            "and https://second-cdn.example/v3-flatcontainer/package/index.json?sig=second-secret.");

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(
            "Credential provider checked https://first-cdn.example/v3-flatcontainer/package/index.json?*** " +
            "and https://second-cdn.example/v3-flatcontainer/package/index.json?***.",
            record.Message);
    }

    [Fact]
    public void DiagnosticNuGetLogger_ReferenceCountsOverlappingSensitiveSources()
    {
        const string sharedSource = "https://feed.example/v3/index.json?sig=shared-secret";
        const string nextSource = "https://next.example/v3/index.json?sig=next-secret";
        var logger = new FakeLogger<NuGetClient>();
        var diagnosticLogger = new NuGetClient.DiagnosticNuGetLogger(logger);

        var first = diagnosticLogger.RegisterSensitiveSources([sharedSource]);
        var second = diagnosticLogger.RegisterSensitiveSources([sharedSource]);

        first.Dispose();
        first.Dispose();
        diagnosticLogger.LogDebug($"Credential provider checked {sharedSource}.");

        second.Dispose();
        using (diagnosticLogger.RegisterSensitiveSources([nextSource]))
        {
            diagnosticLogger.LogDebug($"Credential provider checked {sharedSource} and {nextSource}.");
        }

        Assert.Collection(
            logger.Collector.GetSnapshot(),
            record => Assert.Equal(
                "Credential provider checked https://feed.example/v3/index.json.",
                record.Message),
            record => Assert.Equal(
                $"Credential provider checked {sharedSource} and https://next.example/v3/index.json.",
                record.Message));
    }

    [Fact]
    public void SearchAsync_ResetsNuGetStateWhenComplete()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        CreatePackage(feedDirectory.FullName, "Aspire.Test.Package");

        RemoteExecutor.Invoke(
            static async (feedPath, workingDirectory) =>
            {
                var client = new NuGetClient(new TestFeatures(), new TestEnvironment(), NullLogger<NuGetClient>.Instance);

                var results = await client.SearchAsync(
                    "Aspire.Test.Package",
                    prerelease: false,
                    take: 1000,
                    [feedPath],
                    nugetConfigPath: null,
                    workingDirectory,
                    CancellationToken.None);

                Assert.Single(results);
                Assert.Null(HttpHandlerResourceV3.CredentialService);
            },
            feedDirectory.FullName,
            workspace.WorkspaceRoot.FullName).Dispose();
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
            await client.RestoreAsync(
                [(packageId, "[1.0.0]")],
                "net10.0",
                "win-x64",
                restoreDirectory.FullName,
                [],
                nugetConfigPath,
                workspace.WorkspaceRoot.FullName,
                TestContext.Current.CancellationToken);
            var restoredPackages = ReadRestoredPackages(restoreDirectory.FullName,
                nugetConfigPath, workspace.WorkspaceRoot.FullName);
            packageRoot = Path.GetDirectoryName(restoredPackages[0].InstallPath);
            var manifestPath = Path.Combine(restoreDirectory.FullName, IntegrationPackageProbeManifest.FileName);
            await client.WriteManifestAsync(
                Path.Combine(restoreDirectory.FullName, LockFileFormat.AssetsFileName),
                manifestPath,
                "net10.0",
                "win-x64",
                TestContext.Current.CancellationToken);

            // The manifest points at assets in the global packages folder; older CLIs copied them into a libs
            // directory beside it instead.
            Assert.False(Directory.Exists(Path.Combine(restoreDirectory.FullName, "libs")));
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
    public void RestoreAsync_RespectsNuGetPackagesEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var environmentPackagesDirectory = workspace.CreateDirectory("env-packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(feedDirectory.FullName, packageId);

        // No globalPackagesFolder entry: this covers the environment override specifically, which is
        // resolved by NuGet's settings rather than by anything this client passes explicitly.
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        File.WriteAllText(
            nugetConfigPath,
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{feedDirectory.FullName}" />
              </packageSources>
            </configuration>
            """);

        // The restore runs in a child process because NUGET_PACKAGES is read once when NuGet's
        // settings are first loaded, and the test host has its own value pointing at a shared cache.
        // The variable is set both on the child's start info and again inside the child: passing it
        // through start info alone did not reach the child reliably when other classes ran alongside
        // this one.
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["NUGET_PACKAGES"] = environmentPackagesDirectory.FullName;

        RemoteExecutor.Invoke(
            static async (packageId, configPath, restorePath, workingDirectory, packagesDirectory) =>
            {
                Environment.SetEnvironmentVariable("NUGET_PACKAGES", packagesDirectory);

                var client = new NuGetClient(
                    new TestFeatures(),
                    new TestEnvironment(),
                    NullLogger<NuGetClient>.Instance);

                await client.RestoreAsync(
                    [(packageId, "[1.0.0]")],
                    "net10.0",
                    runtimeIdentifier: null,
                    restorePath,
                    [],
                    configPath,
                    workingDirectory,
                    CancellationToken.None);
            },
            packageId,
            nugetConfigPath,
            restoreDirectory.FullName,
            workspace.WorkspaceRoot.FullName,
            environmentPackagesDirectory.FullName,
            options).Dispose();

        // NuGet records the resolved packages folder in the assets file, which is also what feeds
        // the restore cache key, so assert on it rather than only on the extracted files.
        var assets = new LockFileFormat().Read(Path.Combine(restoreDirectory.FullName, LockFileFormat.AssetsFileName));
        Assert.Contains(
            assets.PackageFolders,
            folder => string.Equals(
                Path.TrimEndingDirectorySeparator(folder.Path),
                Path.TrimEndingDirectorySeparator(environmentPackagesDirectory.FullName),
                StringComparison.OrdinalIgnoreCase));

        // The package must actually land under the override, not merely be referenced from it.
        Assert.True(
            Directory.Exists(Path.Combine(environmentPackagesDirectory.FullName, packageId.ToLowerInvariant(), "1.0.0")),
            $"Expected '{packageId}' to be installed under the NUGET_PACKAGES directory.");
    }

    [Fact]
    public void RestoreAsync_RespectsNuGetConfigGlobalPackagesFolder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var configPackagesDirectory = workspace.CreateDirectory("config-packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(feedDirectory.FullName, packageId);
        var nugetConfigPath = CreateLocalFeedConfig(workspace, feedDirectory, configPackagesDirectory);

        // NUGET_PACKAGES takes precedence over globalPackagesFolder, and the test host often sets it to a shared
        // cache, so the restore runs in a child process without it. The variable is cleared inside the child
        // as well, for the same reason the NUGET_PACKAGES test sets it there.
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment.Remove("NUGET_PACKAGES");

        RemoteExecutor.Invoke(
            static async (packageId, configPath, restorePath, workingDirectory) =>
            {
                Environment.SetEnvironmentVariable("NUGET_PACKAGES", null);

                var client = new NuGetClient(
                    new TestFeatures(),
                    new TestEnvironment(),
                    NullLogger<NuGetClient>.Instance);

                await client.RestoreAsync(
                    [(packageId, "[1.0.0]")],
                    "net10.0",
                    runtimeIdentifier: null,
                    restorePath,
                    [],
                    configPath,
                    workingDirectory,
                    CancellationToken.None);
            },
            packageId,
            nugetConfigPath,
            restoreDirectory.FullName,
            workspace.WorkspaceRoot.FullName,
            options).Dispose();

        var assets = new LockFileFormat().Read(Path.Combine(restoreDirectory.FullName, LockFileFormat.AssetsFileName));
        Assert.Contains(
            assets.PackageFolders,
            folder => string.Equals(
                Path.TrimEndingDirectorySeparator(folder.Path),
                Path.TrimEndingDirectorySeparator(configPackagesDirectory.FullName),
                StringComparison.OrdinalIgnoreCase));
        Assert.True(
            Directory.Exists(Path.Combine(configPackagesDirectory.FullName, packageId.ToLowerInvariant(), "1.0.0")),
            $"Expected '{packageId}' to be installed under the configured globalPackagesFolder.");
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
        using var restoredPackageScope = new RestoredPackageScope(
            GetEffectiveGlobalPackagesFolder(nugetConfigPath: null, workspace.WorkspaceRoot.FullName),
            packageId);

        await client.RestoreAsync(
            [(packageId, "1.0.0")],
            "net10.0",
            "linux-x64",
            restoreDirectory.FullName,
            [feedDirectory.FullName],
            nugetConfigPath: null,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);
        var restoredPackages = ReadRestoredPackages(restoreDirectory.FullName,
            nugetConfigPath: null, workspace.WorkspaceRoot.FullName);
        Assert.NotEmpty(restoredPackages);
        var manifestPath = Path.Combine(restoreDirectory.FullName, IntegrationPackageProbeManifest.FileName);
        await client.WriteManifestAsync(
            Path.Combine(restoreDirectory.FullName, LockFileFormat.AssetsFileName),
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

    [Fact]
    public async Task RestoreAndWriteManifestAsync_WritesCanonicalPackageIdForLowercaseRequest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var packageId = $"Aspire.Test.Package.{Guid.NewGuid():N}";
        CreatePackage(
            feedDirectory.FullName,
            packageId,
            additionalEntries: new Dictionary<string, string>
            {
                ["lib/net10.0/Aspire.Test.Package.xml"] = "<doc />"
            });
        var nugetConfigPath = CreateLocalFeedConfig(workspace, feedDirectory, packagesDirectory);
        using var restoredPackageScope = new RestoredPackageScope(
            GetEffectiveGlobalPackagesFolder(nugetConfigPath, workspace.WorkspaceRoot.FullName),
            packageId);

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        // Package IDs are case-insensitive, so a request may use any casing. The manifest's package ID must
        // come from the restored package, not from the request.
        await client.RestoreAsync(
            [(packageId.ToLowerInvariant(), "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);
        var restoredPackage = Assert.Single(ReadRestoredPackages(restoreDirectory.FullName,
            nugetConfigPath, workspace.WorkspaceRoot.FullName));
        var manifestPath = Path.Combine(restoreDirectory.FullName, IntegrationPackageProbeManifest.FileName);
        await client.WriteManifestAsync(
            Path.Combine(restoreDirectory.FullName, LockFileFormat.AssetsFileName),
            manifestPath,
            "net10.0",
            runtimeIdentifier: null,
            TestContext.Current.CancellationToken);

        // Without a runtime identifier, the assets selected depend on the RID of the machine running the test,
        // so check every entry rather than a fixed list.
        var manifest = IntegrationPackageProbeManifest.Load(manifestPath);
        Assert.NotEmpty(manifest.ManagedAssemblies);
        Assert.All(manifest.ManagedAssemblies, assembly =>
        {
            Assert.Equal(packageId, assembly.PackageId);
            Assert.StartsWith(restoredPackage.InstallPath, assembly.Path, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".dll", assembly.Path, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task RestoreAsync_AppendsExplicitSourcesToConfiguredSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configuredFeed = workspace.CreateDirectory("configured-feed");
        var explicitFeed = workspace.CreateDirectory("explicit-feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var configuredPackageId = $"Aspire.Test.Configured.{Guid.NewGuid():N}";
        var explicitPackageId = $"Aspire.Test.Explicit.{Guid.NewGuid():N}";
        CreatePackage(configuredFeed.FullName, configuredPackageId);
        CreatePackage(explicitFeed.FullName, explicitPackageId);
        var nugetConfigPath = CreateLocalFeedConfig(workspace, configuredFeed, packagesDirectory);
        using var restoredPackageScope = new RestoredPackageScope(
            GetEffectiveGlobalPackagesFolder(nugetConfigPath, workspace.WorkspaceRoot.FullName),
            configuredPackageId,
            explicitPackageId);

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        // Each package exists on only one feed, so the restore succeeds only if the explicit source is used in
        // addition to the configured one rather than in place of it.
        await client.RestoreAsync(
            [(configuredPackageId, "1.0.0"), (explicitPackageId, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [explicitFeed.FullName],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);
        var restoredPackages = ReadRestoredPackages(restoreDirectory.FullName,
            nugetConfigPath, workspace.WorkspaceRoot.FullName);

        Assert.Equal(
            [configuredPackageId, explicitPackageId],
            restoredPackages.Select(package => package.Id).Order(StringComparer.Ordinal).ToArray());
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
        using var restoredPackageScope = new RestoredPackageScope(
            GetEffectiveGlobalPackagesFolder(nugetConfigPath, workspace.WorkspaceRoot.FullName),
            packageId);

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        await client.RestoreAsync(
            [(packageId, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);
        var restoredPackages = ReadRestoredPackages(restoreDirectory.FullName,
            nugetConfigPath, workspace.WorkspaceRoot.FullName);

        var restoredPackage = Assert.Single(restoredPackages);
        Assert.Equal(
            "mapped-source",
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
        using var restoredPackageScope = new RestoredPackageScope(
            GetEffectiveGlobalPackagesFolder(nugetConfigPath, workspace.WorkspaceRoot.FullName),
            packageId);

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        await client.RestoreAsync(
            [(packageId, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [explicitFeed.FullName],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);
        var restoredPackages = ReadRestoredPackages(restoreDirectory.FullName,
            nugetConfigPath, workspace.WorkspaceRoot.FullName);

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

        var exception = await Assert.ThrowsAsync<NuGetOperationException>(() => client.RestoreAsync(
            [(packageId, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [explicitFeed.FullName],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken));

        Assert.Contains(packageId, exception.Output, StringComparison.Ordinal);
        Assert.Contains("PackageSourceMapping", exception.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAsync_SelectsLowestSatisfyingDependencyVersion()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feedDirectory = workspace.CreateDirectory("feed");
        var packagesDirectory = workspace.CreateDirectory("packages");
        var restoreDirectory = workspace.CreateDirectory("restore");
        var rootPackage = $"Aspire.Test.Package.Root.{Guid.NewGuid():N}";
        var dependencyPackage = $"Aspire.Test.Package.Dep.{Guid.NewGuid():N}";
        var missingPackage = $"Aspire.Test.Package.Missing.{Guid.NewGuid():N}";

        CreatePackage(
            feedDirectory.FullName,
            rootPackage,
            dependencies: [(dependencyPackage, "1.0.0")]);
        CreatePackage(feedDirectory.FullName, dependencyPackage, version: "1.0.0");

        // Newer versions of the dependency are not selectable under DependencyBehavior.Lowest.
        // Expanding them anyway is what made the pre-walk fan out across the whole version history
        // of every transitive package and stall restore (#19847), so they declare a dependency that
        // does not exist to keep them clearly off the selected path.
        CreatePackage(
            feedDirectory.FullName,
            dependencyPackage,
            version: "2.0.0",
            dependencies: [(missingPackage, "1.0.0")]);
        CreatePackage(
            feedDirectory.FullName,
            dependencyPackage,
            version: "3.0.0",
            dependencies: [(missingPackage, "1.0.0")]);
        var nugetConfigPath = CreateWorkspaceGlobalPackagesConfig(workspace, packagesDirectory);
        using var restoredPackageScope = new RestoredPackageScope(
            GetEffectiveGlobalPackagesFolder(nugetConfigPath, workspace.WorkspaceRoot.FullName),
            rootPackage,
            dependencyPackage);

        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        await client.RestoreAsync(
            [(rootPackage, "1.0.0")],
            "net10.0",
            runtimeIdentifier: null,
            restoreDirectory.FullName,
            [feedDirectory.FullName],
            nugetConfigPath,
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);
        var restoredPackages = ReadRestoredPackages(restoreDirectory.FullName,
            nugetConfigPath, workspace.WorkspaceRoot.FullName);

        Assert.Collection(
            restoredPackages.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase),
            package =>
            {
                Assert.Equal(dependencyPackage, package.Id);
                Assert.Equal("1.0.0", package.Version);
            },
            package =>
            {
                Assert.Equal(rootPackage, package.Id);
                Assert.Equal("1.0.0", package.Version);
            });
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
            await client.RestoreAsync(
                [(packageId, "1.0.0")],
                "net10.0",
                runtimeIdentifier: null,
                restoreDirectory.FullName,
                [],
                nugetConfigPath,
                workspace.WorkspaceRoot.FullName,
                TestContext.Current.CancellationToken);
            var package = Assert.Single(ReadRestoredPackages(restoreDirectory.FullName, nugetConfigPath, workspace.WorkspaceRoot.FullName));

            Assert.Equal(incompleteInstallPath, package.InstallPath, ignoreCase: true);
            Assert.True(File.Exists(Path.Combine(package.InstallPath, ".nupkg.metadata")));
            Assert.True(File.Exists(Path.Combine(package.InstallPath, "lib", "net10.0", "Aspire.Test.Package.dll")));
        }
        finally
        {
            Directory.Delete(Path.Combine(globalPackagesFolder, packageId.ToLowerInvariant()), recursive: true);
        }
    }

    [Fact]
    public void GetSettings_ReturnsEffectiveSourcePolicy()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectDirectory = workspace.CreateDirectory("AppHost");
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        var source = $"https://packages.example.com/v3/index.json?token={Guid.NewGuid():N}";
        File.WriteAllText(
            configPath,
            $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="{{source}}" />
              </packageSources>
              <disabledPackageSources>
                <add key="private" value="true" />
              </disabledPackageSources>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="Aspire.*" />
                </packageSource>
              </packageSourceMapping>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var settings = client.GetSettings(
            projectDirectory.FullName,
            Enumerable.Repeat((byte)0x5A, NuGetSourceIdentity.KeySizeInBytes).ToArray());

        Assert.Contains(configPath, settings.ConfigPaths);
        var sourceInfo = Assert.Single(settings.Sources, source => source.Name == "private");
        Assert.False(sourceInfo.IsEnabled);
        Assert.True(sourceInfo.HasCredentials);
        Assert.Contains(source, settings.SensitiveSourceValues);
        Assert.True(settings.PackageSourceMappingEnabled);
        var mapping = Assert.Single(settings.PackageSourceMappings);
        Assert.Equal("private", mapping.SourceKey);
        Assert.Equal(["Aspire.*"], mapping.Patterns);
        Assert.Contains("private", settings.DisabledPackageSourceKeys);
        Assert.Contains("private", settings.ReservedPackageSourceKeys);
        Assert.NotEmpty(settings.CacheIdentity);
    }

    [Fact]
    public void GetSettings_TreatsCredentialBearingSourceNameAsSensitive()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectDirectory = workspace.CreateDirectory("AppHost");
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        const string sourceName = "https://user:credential-marker@packages.example.com";
        const string source = "https://packages.example.com/v3/index.json";
        File.WriteAllText(
            configPath,
            $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="{{sourceName}}" value="{{source}}" />
              </packageSources>
            </configuration>
            """);
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        var settings = client.GetSettings(
            projectDirectory.FullName,
            Enumerable.Repeat((byte)0x5A, NuGetSourceIdentity.KeySizeInBytes).ToArray());

        var sourceInfo = Assert.Single(settings.Sources);
        Assert.Equal(sourceName, sourceInfo.Name);
        Assert.Contains(sourceName, settings.SensitiveSourceValues);
        Assert.DoesNotContain(source, settings.SensitiveSourceValues);
    }

    [Fact]
    public void GetSettings_TrustedSignerChangeInvalidatesCacheIdentity()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);
        var sourceIdentityKey = Enumerable.Repeat((byte)0x5A, NuGetSourceIdentity.KeySizeInBytes).ToArray();

        WriteTrustedSignerConfig(configPath, new string('A', 64));
        var firstSettings = client.GetSettings(workspace.WorkspaceRoot.FullName, sourceIdentityKey);

        WriteTrustedSignerConfig(configPath, new string('B', 64));
        var secondSettings = client.GetSettings(workspace.WorkspaceRoot.FullName, sourceIdentityKey);

        Assert.NotEqual(firstSettings.CacheIdentity, secondSettings.CacheIdentity);
    }

    [Fact]
    public void WriteConfigOverlay_WritesPolicySections()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "policy", "NuGet.Config");
        var client = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);

        client.WriteConfigOverlay(
            new NuGetConfigOverlayRequest(
                [new("private", "https://packages.example.com/v3/index.json")],
                [new("private", ["Aspire.*"])],
                ClearDisabledPackageSources: true,
                DisabledPackageSourceKeys: ["other"],
                GlobalPackagesFolder: "/tmp/aspire-packages"),
            outputPath);

        var document = XDocument.Load(outputPath);
        Assert.Equal(
            "https://packages.example.com/v3/index.json",
            document.Descendants("packageSources").Elements("add").Single().Attribute("value")?.Value);
        Assert.Equal(
            ["Aspire.*"],
            document.Descendants("packageSourceMapping").Elements("packageSource").Elements("package")
                .Select(static package => package.Attribute("pattern")!.Value));
        var disabledSources = Assert.Single(document.Descendants("disabledPackageSources"));
        Assert.NotNull(disabledSources.Element("clear"));
        Assert.Equal("other", disabledSources.Elements("add").Single().Attribute("key")?.Value);
        Assert.Equal(
            "/tmp/aspire-packages",
            document.Descendants("config").Elements("add").Single().Attribute("value")?.Value);
    }

    /// <summary>
    /// Writes a NuGet config that redirects the global packages folder into the temporary workspace
    /// so restored packages are removed with the workspace instead of accumulating in the machine's
    /// real global packages folder.
    /// </summary>
    private static string CreateWorkspaceGlobalPackagesConfig(TemporaryWorkspace workspace, DirectoryInfo packagesDirectory)
    {
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        File.WriteAllText(
            nugetConfigPath,
            $"""
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packagesDirectory.FullName}" />
              </config>
            </configuration>
            """);

        return nugetConfigPath;
    }

    /// <summary>
    /// Writes a nuget.config with a single local feed and a workspace-scoped global packages folder, so restores
    /// neither read the machine's configured feeds nor add packages to its real global packages folder.
    /// </summary>
    private static string CreateLocalFeedConfig(TemporaryWorkspace workspace, DirectoryInfo feedDirectory, DirectoryInfo packagesDirectory)
    {
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

        return nugetConfigPath;
    }

    private static void WriteTrustedSignerConfig(string configPath, string fingerprint)
    {
        File.WriteAllText(
            configPath,
            $$"""
            <configuration>
              <trustedSigners>
                <author name="test-author">
                  <certificate fingerprint="{{fingerprint}}" hashAlgorithm="SHA256" allowUntrustedRoot="false" />
                </author>
              </trustedSigners>
            </configuration>
            """);
    }

    /// <summary>
    /// Resolves the global packages folder the client will actually restore into, using the same
    /// settings lookup. NUGET_PACKAGES takes precedence over the <c>globalPackagesFolder</c> config
    /// value, so a workspace-scoped config alone does not keep restores out of the machine-wide
    /// folder on developer machines or CI agents that set the variable.
    /// </summary>
    private static string GetEffectiveGlobalPackagesFolder(string? nugetConfigPath, string workingDirectory)
    {
        var settings = nugetConfigPath is not null
            ? Settings.LoadSpecificSettings(Path.GetDirectoryName(nugetConfigPath)!, Path.GetFileName(nugetConfigPath))
            : Settings.LoadDefaultSettings(workingDirectory);

        return SettingsUtility.GetGlobalPackagesFolder(settings);
    }

    /// <summary>
    /// Reads the packages NuGet restored from the assets file it wrote, mirroring how the manifest
    /// step consumes restore output.
    /// </summary>
    private static IReadOnlyList<(string Id, string Version, string InstallPath)> ReadRestoredPackages(
        string restoreDirectory,
        string? nugetConfigPath,
        string workingDirectory)
    {
        var lockFile = new LockFileFormat().Read(Path.Combine(restoreDirectory, LockFileFormat.AssetsFileName));
        var packagesFolder = lockFile.PackageFolders.FirstOrDefault()?.Path
            ?? GetEffectiveGlobalPackagesFolder(nugetConfigPath, workingDirectory);
        var pathResolver = new VersionFolderPathResolver(packagesFolder);

        return lockFile.Libraries
            .Where(library => string.Equals(library.Type, "package", StringComparison.OrdinalIgnoreCase))
            .Select(library => (
                library.Name,
                library.Version.ToNormalizedString(),
                pathResolver.GetInstallPath(library.Name, library.Version)))
            .ToArray();
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

    /// <summary>
    /// Removes the packages a restore test installed from the global packages folder. Disposal runs
    /// even when an assertion fails, so a failing test cannot leave packages behind in a folder that
    /// outlives the temporary workspace.
    /// </summary>
    private sealed class RestoredPackageScope(string globalPackagesFolder, params string[] packageIds) : IDisposable
    {
        public void Dispose()
        {
            foreach (var packageId in packageIds)
            {
                var packageDirectory = Path.Combine(globalPackagesFolder, packageId.ToLowerInvariant());
                if (!Directory.Exists(packageDirectory))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(packageDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another test process may hold the extracted files open. Leaving a stray
                    // package behind must not fail an otherwise passing test.
                }
            }
        }
    }
}

file static class NuGetClientTestExtensions
{
    public static Task RestoreAsync(
        this NuGetClient client,
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
        => client.RestoreAsync(
            packages,
            framework,
            runtimeIdentifier,
            outputPath,
            sources,
            nugetConfigPath is null ? [] : [nugetConfigPath],
            workingDirectory,
            globalPackagesFolderOverride: null,
            sensitiveSources: [],
            cancellationToken);
}
