// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class BundleNuGetServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RestorePackagesAsync_UsesWorkspaceAspireDirectoryAndForwardsInputs()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        File.WriteAllText(nugetConfigPath, "<configuration />");

        string? capturedOutputPath = null;
        IReadOnlyList<string>? capturedConfigPaths = null;
        IReadOnlyList<string>? capturedSources = null;
        string? capturedGlobalPackagesFolder = null;
        var nuGetClient = new FakeNuGetClient
        {
            RestoreCallback = (_, _, _, outputPath, sources, configPaths, _, globalPackagesFolder, _, _) =>
            {
                capturedOutputPath = outputPath;
                capturedConfigPaths = configPaths;
                capturedSources = sources;
                capturedGlobalPackagesFolder = globalPackagesFolder;
                return Task.CompletedTask;
            }
        };
        var service = CreateService(nuGetClient);
        var globalPackagesFolder = Path.Combine(workspace.WorkspaceRoot.FullName, "packages");

        var manifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            sources: ["https://example.com/v3/index.json"],
            nugetConfigPaths: [nugetConfigPath],
            globalPackagesFolderOverride: globalPackagesFolder);

        var restoreRoot = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore");
        Assert.StartsWith(restoreRoot, manifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(manifestPath)!, "obj"), capturedOutputPath);
        Assert.Equal([nugetConfigPath], capturedConfigPaths);
        Assert.Equal(["https://example.com/v3/index.json"], capturedSources);
        Assert.Equal(globalPackagesFolder, capturedGlobalPackagesFolder);
        Assert.Equal(1, nuGetClient.RestoreCallCount);
        Assert.Equal(1, nuGetClient.WriteManifestCallCount);
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsForDifferentSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var service = CreateService(new FakeNuGetClient());

        var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-a/index.json"],
            workingDirectory: appHostDirectory.FullName);
        var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-b/index.json"],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(resultA, resultB);
    }

    [Theory]
    [InlineData("ambient-a", "overlay", "/packages", "ambient-b", "overlay", "/packages")]
    [InlineData("ambient", "overlay-a", "/packages", "ambient", "overlay-b", "/packages")]
    [InlineData("ambient", "overlay", "/packages-a", "ambient", "overlay", "/packages-b")]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsForRestorePolicy(
        string settingsIdentityA,
        string overlayIdentityA,
        string packagesPathA,
        string settingsIdentityB,
        string overlayIdentityB,
        string packagesPathB)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var service = CreateService(new FakeNuGetClient());

        var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            nugetSettingsCacheIdentity: settingsIdentityA,
            nugetConfigOverlayCacheIdentity: overlayIdentityA,
            globalPackagesFolderOverride: packagesPathA);
        var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            nugetSettingsCacheIdentity: settingsIdentityB,
            nugetConfigOverlayCacheIdentity: overlayIdentityB,
            globalPackagesFolderOverride: packagesPathB);

        Assert.NotEqual(resultA, resultB);
    }

    [Fact]
    public void ComputePackageHash_IgnoresSourceOrder()
    {
        var packageList = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };

        var resultA = BundleNuGetService.ComputePackageHash(
            packageList,
            "net10.0",
            runtimeIdentifier: null,
            sources: ["https://example.com/feed-a/index.json", "https://example.com/feed-b/index.json"]);
        var resultB = BundleNuGetService.ComputePackageHash(
            packageList,
            "net10.0",
            runtimeIdentifier: null,
            sources: ["https://example.com/feed-b/index.json", "https://example.com/feed-a/index.json"]);

        Assert.Equal(resultA, resultB);
    }

    [Fact]
    public void ComputePackageHash_DistinguishesSourcesContainingDelimiter()
    {
        var packageList = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };

        var resultA = BundleNuGetService.ComputePackageHash(
            packageList,
            "net10.0",
            runtimeIdentifier: null,
            sources: ["/feeds/a|/feeds/b"]);
        var resultB = BundleNuGetService.ComputePackageHash(
            packageList,
            "net10.0",
            runtimeIdentifier: null,
            sources: ["/feeds/a", "/feeds/b"]);

        Assert.NotEqual(resultA, resultB);
    }

    [Fact]
    public void ComputePackageHash_ChangesWhenRestoreToolChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var toolPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.dll");
        File.WriteAllText(toolPath, "original implementation");
        var packageList = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };

        var originalHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", runtimeIdentifier: null, toolPath);
        File.WriteAllText(toolPath, "updated implementation with a different size");
        var updatedHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", runtimeIdentifier: null, toolPath);

        Assert.NotEqual(originalHash, updatedHash);
    }

    [Fact]
    public void GetRestoreToolPath_UsesCliAssemblyForManagedLaunch()
    {
        var toolPath = BundleNuGetService.GetRestoreToolPath();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, $"{typeof(BundleNuGetService).Assembly.GetName().Name}.dll"),
            toolPath);
        Assert.NotEqual(Environment.ProcessPath, toolPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_RestoreFailureRedactsSensitiveSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        const string sensitiveSource = "https://user:secret@example.com/v3/index.json";
        var output = $"ERROR: Unable to load {sensitiveSource}{Environment.NewLine}";
        var nuGetClient = new FakeNuGetClient
        {
            RestoreCallback = (_, _, _, _, _, _, _, _, _, _) => throw new NuGetOperationException(output)
        };
        var service = CreateService(nuGetClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestorePackagesAsync(
            [("Missing.Package", "1.0.0")],
            workingDirectory: appHostDirectory.FullName,
            additionalSensitiveSources: [sensitiveSource]));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("example.com/v3/index.json", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, nuGetClient.WriteManifestCallCount);
    }

    [Fact]
    public async Task RestorePackagesAsync_ManifestFailureReportsNuGetOutput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var output = "Error: Assets file not found." + Environment.NewLine;
        var nuGetClient = new FakeNuGetClient
        {
            WriteManifestCallback = (_, _, _, _, _) => throw new NuGetOperationException(output)
        };
        var service = CreateService(nuGetClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName));

        Assert.Equal($"Manifest creation failed: {output}", exception.Message);
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesCachedValidManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var packageList = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };
        var manifestPath = Path.Combine(GetRestoreDirectory(workspace, packageList), ManifestFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, """{"managedAssemblies":[],"nativeLibraries":[]}""");
        var nuGetClient = new FakeNuGetClient();
        var service = CreateService(nuGetClient);

        var result = await service.RestorePackagesAsync(
            packageList,
            workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result);
        Assert.Equal(0, nuGetClient.RestoreCallCount);
        Assert.Equal(0, nuGetClient.WriteManifestCallCount);
    }

    [Fact]
    public async Task RestorePackagesAsync_RegeneratesInvalidCachedManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var packageList = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };
        var manifestPath = Path.Combine(GetRestoreDirectory(workspace, packageList), ManifestFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{ invalid json");
        var nuGetClient = new FakeNuGetClient();
        var service = CreateService(nuGetClient);

        var result = await service.RestorePackagesAsync(
            packageList,
            workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result);
        Assert.Equal(1, nuGetClient.RestoreCallCount);
        Assert.Equal(1, nuGetClient.WriteManifestCallCount);
        Assert.Equal("""{"managedAssemblies":[],"nativeLibraries":[]}""", File.ReadAllText(manifestPath));
    }

    [Fact]
    public async Task RestorePackagesAsync_SharesRestoreCacheAcrossAppHostsInSameWorkspace()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstAppHost = workspace.CreateDirectory(Path.Combine("apps", "api"));
        var secondAppHost = workspace.CreateDirectory(Path.Combine("apps", "web"));
        var nuGetClient = new FakeNuGetClient();
        var service = CreateService(nuGetClient);
        var restoreRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore");

        var firstManifest = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: firstAppHost.FullName);
        var secondManifest = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, firstManifest, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstManifest, secondManifest);
        Assert.Equal(1, nuGetClient.RestoreCallCount);

        var divergedManifest = await service.RestorePackagesAsync(
            [("Aspire.Hosting.Python", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, divergedManifest, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(secondManifest, divergedManifest);
        Assert.Equal(2, nuGetClient.RestoreCallCount);
    }

    [Fact]
    public async Task RestorePackagesAsync_IgnoresLockedLegacyLibsDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var packageList = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };
        var restoreDirectory = GetRestoreDirectory(workspace, packageList);
        var legacyLibsDirectory = Directory.CreateDirectory(Path.Combine(restoreDirectory, "libs"));
        var lockedFilePath = Path.Combine(legacyLibsDirectory.FullName, "Microsoft.Extensions.DependencyInjection.xml");
        File.WriteAllText(lockedFilePath, "legacy");
        var nuGetClient = new FakeNuGetClient();
        var service = CreateService(nuGetClient);

        using var lockedFile = new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await service.RestorePackagesAsync(
            packageList,
            workingDirectory: appHostDirectory.FullName);

        Assert.Equal(Path.Combine(restoreDirectory, ManifestFileName), result);
        Assert.Equal(1, nuGetClient.RestoreCallCount);
        Assert.Equal(1, nuGetClient.WriteManifestCallCount);
    }

    [Fact]
    public async Task RestorePackagesAsync_SerializesConcurrentRestoreForSameCachePath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var firstRestoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRestoreToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nuGetClient = new FakeNuGetClient
        {
            RestoreCallback = async (_, _, _, _, _, _, _, _, _, cancellationToken) =>
            {
                firstRestoreStarted.TrySetResult();
                await allowFirstRestoreToComplete.Task.WaitAsync(cancellationToken);
            }
        };
        var service = CreateService(nuGetClient);
        var packageList = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };

        var firstRestoreTask = service.RestorePackagesAsync(
            packageList,
            workingDirectory: appHostDirectory.FullName);
        await firstRestoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondRestoreTask = service.RestorePackagesAsync(
            packageList,
            workingDirectory: appHostDirectory.FullName);
        allowFirstRestoreToComplete.SetResult();

        var manifests = await Task.WhenAll(firstRestoreTask, secondRestoreTask);

        Assert.Equal(manifests[0], manifests[1]);
        Assert.Equal(1, nuGetClient.RestoreCallCount);
        Assert.Equal(1, nuGetClient.WriteManifestCallCount);
    }

    private const string ManifestFileName = "integration-package-probe-manifest.json";

    private static BundleNuGetService CreateService(INuGetClient nuGetClient)
        => new(
            NullLogger<BundleNuGetService>.Instance,
            nuGetClient);

    private static string GetRestoreDirectory(
        TemporaryWorkspace workspace,
        List<(string Id, string Version)> packages)
    {
        var packageHash = BundleNuGetService.ComputePackageHash(
            packages,
            "net10.0",
            runtimeIdentifier: null,
            BundleNuGetService.GetRestoreToolPath());

        return Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            packageHash);
    }
}
