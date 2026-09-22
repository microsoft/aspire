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
        string? capturedConfigPath = null;
        IReadOnlyList<string>? capturedSources = null;
        var nuGetClient = new FakeNuGetClient
        {
            RestoreCallback = (_, _, _, outputPath, sources, configPath, _, _) =>
            {
                capturedOutputPath = outputPath;
                capturedConfigPath = configPath;
                capturedSources = sources;
                return Task.CompletedTask;
            }
        };
        var service = CreateService(nuGetClient);

        var manifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            sources: ["https://example.com/v3/index.json"],
            nugetConfigPath: nugetConfigPath);

        var restoreRoot = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore");
        Assert.StartsWith(restoreRoot, manifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(manifestPath)!, "obj"), capturedOutputPath);
        Assert.Equal(nugetConfigPath, capturedConfigPath);
        Assert.Equal(["https://example.com/v3/index.json"], capturedSources);
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
    public async Task RestorePackagesAsync_RestoreFailureReportsHelperOutput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var output = "ERROR: NU1101: Unable to find package Missing.Package." + Environment.NewLine +
            "Error: Restore failed: NU1101: Unable to find package Missing.Package." + Environment.NewLine;
        var nuGetClient = new FakeNuGetClient
        {
            RestoreCallback = (_, _, _, _, _, _, _, _) => throw new NuGetOperationException(output)
        };
        var service = CreateService(nuGetClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestorePackagesAsync(
            [("Missing.Package", "1.0.0")],
            workingDirectory: appHostDirectory.FullName));

        Assert.Equal($"Package restore failed: {output}", exception.Message);
        Assert.Equal(0, nuGetClient.WriteManifestCallCount);
    }

    [Fact]
    public async Task RestorePackagesAsync_ManifestFailureReportsHelperOutput()
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
        var packageHash = BundleNuGetService.ComputePackageHash(
            packageList,
            "net10.0",
            runtimeIdentifier: null,
            Environment.ProcessPath);
        var manifestPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            packageHash,
            "integration-package-probe-manifest.json");
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
    public async Task RestorePackagesAsync_SerializesConcurrentRestoreForSameCachePath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var firstRestoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRestoreToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nuGetClient = new FakeNuGetClient
        {
            RestoreCallback = async (_, _, _, _, _, _, _, cancellationToken) =>
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

    private static BundleNuGetService CreateService(INuGetClient nuGetClient)
    {
        return new BundleNuGetService(
            NullLogger<BundleNuGetService>.Instance,
            nuGetClient);
    }
}
