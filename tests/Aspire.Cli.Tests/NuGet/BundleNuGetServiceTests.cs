// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.NuGet;

public class BundleNuGetServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RestorePackagesAsync_UsesWorkspaceAspireDirectoryForRestoreArtifacts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var restoreResult = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);
        var manifestPath = restoreResult.ManifestPath;

        var restoreRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore");
        var restoreDirectory = Directory.GetParent(manifestPath)!.FullName;

        Assert.StartsWith(restoreRoot, manifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, invocations.Count);
        Assert.Equal(Path.Combine(restoreDirectory, "obj"), GetArgumentValue(invocations[0], "--output"));
        Assert.Equal("manifest", invocations[1][1]);
        Assert.Equal(manifestPath, GetArgumentValue(invocations[1], "--output"));
        Assert.Equal(Path.Combine(restoreDirectory, "obj", "project.assets.json"), GetArgumentValue(invocations[1], "--assets"));
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsForDifferentSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var executionFactory = new TestProcessExecutionFactory();
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-a/index.json"],
            workingDirectory: appHostDirectory.FullName);

        using var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-b/index.json"],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(resultA.ManifestPath, resultB.ManifestPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsForDifferentGlobalPackagesFolders()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var environmentVariables = new Dictionary<string, string?>
        {
            [CliPathHelper.NuGetPackagesEnvironmentVariable] = Path.Combine(workspace.WorkspaceRoot.FullName, "packages-a")
        };
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(environmentVariables),
            NullLogger<BundleNuGetService>.Instance);

        using var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);
        environmentVariables[CliPathHelper.NuGetPackagesEnvironmentVariable] =
            Path.Combine(workspace.WorkspaceRoot.FullName, "packages-b");
        using var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(resultA.ManifestPath, resultB.ManifestPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsForDifferentFallbackPackageFolderOrder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var fallbackA = Path.Combine(workspace.WorkspaceRoot.FullName, "fallback-a");
        var fallbackB = Path.Combine(workspace.WorkspaceRoot.FullName, "fallback-b");
        var environmentVariables = new Dictionary<string, string?>
        {
            [CliPathHelper.NuGetFallbackPackagesEnvironmentVariable] = $"{fallbackA};{fallbackB}"
        };
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(environmentVariables),
            NullLogger<BundleNuGetService>.Instance);

        using var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);
        environmentVariables[CliPathHelper.NuGetFallbackPackagesEnvironmentVariable] = $"{fallbackB};{fallbackA}";
        using var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(resultA.ManifestPath, resultB.ManifestPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_ExplicitGlobalPackagesFolderOverridesInheritedEnvironment()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var inheritedPackagesFolder = Path.Combine(workspace.WorkspaceRoot.FullName, "inherited-packages");
        var stagingPackagesFolder = Path.Combine(workspace.WorkspaceRoot.FullName, "staging-packages");
        var environmentVariables = new Dictionary<string, string?>
        {
            [CliPathHelper.NuGetPackagesEnvironmentVariable] = inheritedPackagesFolder
        };
        var executionFactory = new TestProcessExecutionFactory();
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(environmentVariables),
            NullLogger<BundleNuGetService>.Instance);

        using var firstResult = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            globalPackagesFolderOverride: stagingPackagesFolder);
        environmentVariables[CliPathHelper.NuGetPackagesEnvironmentVariable] =
            Path.Combine(workspace.WorkspaceRoot.FullName, "different-inherited-packages");
        using var secondResult = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            globalPackagesFolderOverride: stagingPackagesFolder);

        Assert.Equal(firstResult.ManifestPath, secondResult.ManifestPath);
        Assert.Equal(stagingPackagesFolder, executionFactory.LastEnvironmentVariables?[CliPathHelper.NuGetPackagesEnvironmentVariable]);
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsForDifferentNuGetConfigs()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var firstConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "first.config");
        var secondConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "second.config");
        await File.WriteAllTextAsync(firstConfigPath, """
            <configuration>
              <packageSourceMapping>
                <packageSource key="shared"><package pattern="Aspire.*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        await File.WriteAllTextAsync(secondConfigPath, """
            <configuration>
              <packageSourceMapping>
                <packageSource key="shared"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/shared/index.json"],
            nugetConfigPath: firstConfigPath,
            workingDirectory: appHostDirectory.FullName);
        using var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/shared/index.json"],
            nugetConfigPath: secondConfigPath,
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(resultA.ManifestPath, resultB.ManifestPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_DoesNotReuseCredentialBearingNuGetConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "credentialed.config");
        await File.WriteAllTextAsync(configPath, """
            <configuration>
              <config>
                <add key="http_proxy" value="https://user:password@example.invalid" />
              </config>
            </configuration>
            """);

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var firstResult = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            nugetConfigPath: configPath,
            workingDirectory: appHostDirectory.FullName);
        using var secondResult = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            nugetConfigPath: configPath,
            workingDirectory: appHostDirectory.FullName);

        var temporaryRoot = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            BundleNuGetService.TemporaryCredentialRestoreDirectoryName);
        Assert.NotEqual(firstResult.ManifestPath, secondResult.ManifestPath);
        Assert.True(firstResult.IsTemporary);
        Assert.True(secondResult.IsTemporary);
        Assert.StartsWith(temporaryRoot, firstResult.ManifestPath, StringComparisons.FileSystemPath);
        Assert.StartsWith(temporaryRoot, secondResult.ManifestPath, StringComparisons.FileSystemPath);
        Assert.True(Directory.Exists(Directory.GetParent(firstResult.ManifestPath)!.FullName));
        Assert.True(Directory.Exists(Directory.GetParent(secondResult.ManifestPath)!.FullName));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(temporaryRoot));
        }
    }

    [Fact]
    public async Task RestorePackagesAsync_DoesNotReuseCredentialBearingSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        const string credentialBearingSource = "https://packages.example.com/v3/index.json?sig=secret";

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var firstResult = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: [credentialBearingSource],
            workingDirectory: appHostDirectory.FullName);
        using var secondResult = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: [credentialBearingSource],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(firstResult.ManifestPath, secondResult.ManifestPath);
        Assert.True(firstResult.IsTemporary);
        Assert.True(secondResult.IsTemporary);
    }

    [Fact]
    public async Task RestorePackagesAsync_CredentialBearingSourceUsesLeasedGlobalPackagesFolder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        const string credentialBearingSource = "https://packages.example.com/v3/index.json?sig=secret";
        var persistentPackagesFolder = Path.Combine(workspace.WorkspaceRoot.FullName, "persistent-packages");
        var executionFactory = new TestProcessExecutionFactory();
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var result = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: [credentialBearingSource],
            workingDirectory: appHostDirectory.FullName,
            globalPackagesFolderOverride: persistentPackagesFolder);

        var restoreDirectory = Directory.GetParent(result.ManifestPath)!.FullName;
        Assert.True(result.IsTemporary);
        Assert.Equal(
            Path.Combine(restoreDirectory, "packages"),
            executionFactory.LastEnvironmentVariables?[CliPathHelper.NuGetPackagesEnvironmentVariable]);
    }

    [Fact]
    public async Task RestorePackagesAsync_RemovesAbandonedCredentialRestoreDirectories()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var temporaryRoot = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            BundleNuGetService.TemporaryCredentialRestoreDirectoryName);
        var abandonedDirectory = Path.Combine(
            temporaryRoot,
            $".{BundleNuGetService.TemporaryCredentialRestoreDirectoryPrefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(abandonedDirectory);
        File.WriteAllText(Path.Combine(abandonedDirectory, "project.assets.json"), "credential-bearing restore metadata");

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var result = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://packages.example.com/v3/index.json?sig=secret"],
            workingDirectory: appHostDirectory.FullName);

        Assert.False(Directory.Exists(abandonedDirectory));
        Assert.False(File.Exists(TemporaryCacheDirectory.GetLeasePath(abandonedDirectory)));
        Assert.True(Directory.Exists(Directory.GetParent(result.ManifestPath)!.FullName));
    }

    [Fact]
    public async Task RestorePackagesAsync_RedactsCredentialBearingSourcesFromFailures()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        const string credentialBearingSource = "https://user:password@packages.example.com/v3/index.json?sig=secret";
        string? restoreOutputPath = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            CreateExecutionCallback = (args, environment, _, options) =>
            {
                restoreOutputPath = GetArgumentValue(args, "--output");
                return new TestProcessExecution(
                    "aspire-managed",
                    args,
                    environment,
                    options,
                    (_, _, _) => Task.FromResult((0, (string?)null)),
                    () => 1)
                {
                    WaitForExitAsyncCallback = (invocationOptions, _) =>
                    {
                        invocationOptions.StandardErrorCallback?.Invoke($"Unable to load the service index for source {credentialBearingSource}.");
                        return Task.FromResult(1);
                    }
                };
            }
        };
        var sink = new TestSink();
        var logger = new TestLogger<BundleNuGetService>(new TestLoggerFactory(sink, enabled: true));
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            logger);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: [credentialBearingSource],
            workingDirectory: appHostDirectory.FullName));

        Assert.DoesNotContain(credentialBearingSource, exception.Message);
        Assert.Contains("packages.example.com", exception.Message);
        Assert.DoesNotContain(sink.Writes, write => write.Message?.Contains(credentialBearingSource, StringComparison.Ordinal) == true);
        Assert.NotNull(restoreOutputPath);
        Assert.False(Directory.Exists(Directory.GetParent(restoreOutputPath)!.FullName));
    }

    [Fact]
    public async Task GetNuGetConfigPathsAsync_UsesBundledHelper()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var configPath = Path.Combine(appHostDirectory.FullName, "NuGet.Config");
        string[]? invocation = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocation = args,
            AttemptCallback = (_, _) => (0, System.Text.Json.JsonSerializer.Serialize(new[] { configPath }))
        };
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        var configPaths = await service.GetNuGetConfigPathsAsync(appHostDirectory.FullName, CancellationToken.None);

        Assert.Equal([configPath], configPaths);
        Assert.Equal(["nuget", "config-paths", "--working-dir", appHostDirectory.FullName], invocation!);
    }

    [Fact]
    public async Task RestorePackagesAsync_PassesNuGetConfigToRestore()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        File.WriteAllText(nugetConfigPath, "<configuration />");

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var result = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            nugetConfigPath: nugetConfigPath);

        Assert.Equal(nugetConfigPath, GetArgumentValue(invocations[0], "--nuget-config"));
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesCachedManifestWithoutRunningHelper()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var packageList = new List<(string Id, string Version)> { ("Aspire.Hosting.JavaScript", "9.4.0") };
        var packageHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", null, managedPath);
        var manifestPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            packageHash,
            "integration-package-probe-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{}");

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result.ManifestPath);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task RestorePackagesAsync_RegeneratesCachedManifestWhenManifestIsInvalid()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var packageList = new List<(string Id, string Version)> { ("Aspire.Hosting.JavaScript", "9.4.0") };
        var packageHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", null, managedPath);
        var manifestPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            packageHash,
            "integration-package-probe-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{ invalid json");

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) =>
            {
                invocations.Add(args.ToArray());
                if (args.Contains("manifest"))
                {
                    File.WriteAllText(manifestPath, """{"managedAssemblies":[],"nativeLibraries":[]}""");
                }
            }
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result.ManifestPath);
        Assert.Equal(2, invocations.Count);
        Assert.Equal("restore", invocations[0][1]);
        Assert.Equal("manifest", invocations[1][1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestorePackagesAsync_RegeneratesCachedManifestWhenReferencedAssetIsMissing(bool managedAsset)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var packageList = new List<(string Id, string Version)> { ("Aspire.Hosting.JavaScript", "9.4.0") };
        var packageHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", null, managedPath);
        var manifestPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "integrations",
            "package-restore",
            packageHash,
            IntegrationPackageProbeManifest.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);

        var missingAssetPath = Path.Combine(workspace.WorkspaceRoot.FullName, "cleared-packages", "missing.dll");
        var staleManifest = managedAsset
            ? IntegrationPackageProbeManifest.Create(
                [new IntegrationPackageManagedAssembly { Name = "Missing", Path = missingAssetPath }],
                [])
            : IntegrationPackageProbeManifest.Create(
                [],
                [new IntegrationPackageNativeLibrary { FileName = "missing.dll", Path = missingAssetPath }]);
        await IntegrationPackageProbeManifest.WriteAsync(manifestPath, staleManifest);

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) =>
            {
                invocations.Add(args.ToArray());
                if (args.Contains("manifest"))
                {
                    File.WriteAllText(manifestPath, """{"managedAssemblies":[],"nativeLibraries":[]}""");
                }
            }
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result.ManifestPath);
        Assert.Equal(2, invocations.Count);
        Assert.Equal("restore", invocations[0][1]);
        Assert.Equal("manifest", invocations[1][1]);
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsWhenManagedHelperChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, "v1");

        var executionFactory = new TestProcessExecutionFactory();
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var resultA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        File.WriteAllText(managedPath, "v2-changed");

        using var resultB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(resultA.ManifestPath, resultB.ManifestPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_SharesRestoreCacheAcrossAppHostsInSameWorkspace()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var firstAppHost = workspace.CreateDirectory(Path.Combine("apps", "api"));
        var secondAppHost = workspace.CreateDirectory(Path.Combine("apps", "web"));
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var executionFactory = new TestProcessExecutionFactory();
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        var restoreRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore");

        // Same packages + sources across two apphosts in one workspace should share the cache.
        using var sharedManifestFirst = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: firstAppHost.FullName);
        using var sharedManifestSecond = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, sharedManifestFirst.ManifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(restoreRoot, sharedManifestSecond.ManifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sharedManifestFirst.ManifestPath, sharedManifestSecond.ManifestPath);

        // Different package sets must NOT collide even when workspace is shared.
        using var divergedManifest = await service.RestorePackagesAsync(
            [("Aspire.Hosting.Python", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, divergedManifest.ManifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(sharedManifestSecond.ManifestPath, divergedManifest.ManifestPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_SerializesConcurrentRestoreForSameCachePath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var invocations = new ConcurrentQueue<string[]>();
        var firstRestoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRestoreToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreAttemptCount = 0;
        var manifestAttemptCount = 0;

        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Enqueue(args.ToArray()),
            AsyncAttemptCallback = async (attempt, _, cancellationToken) =>
            {
                var args = invocations.ElementAt(attempt - 1);
                if (args.Contains("restore"))
                {
                    if (Interlocked.Increment(ref restoreAttemptCount) == 1)
                    {
                        firstRestoreStarted.SetResult();
                        await allowFirstRestoreToComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return (0, null);
                }

                if (args.Contains("manifest"))
                {
                    Interlocked.Increment(ref manifestAttemptCount);
                    await File.WriteAllTextAsync(
                        GetArgumentValue(args, "--output"),
                        """{"managedAssemblies":[],"nativeLibraries":[]}""",
                        cancellationToken).ConfigureAwait(false);
                }

                return (0, null);
            }
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        var packageList = new List<(string Id, string Version)> { ("Aspire.Hosting.JavaScript", "9.4.0") };
        var firstRestoreTask = service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);
        await firstRestoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondRestoreTask = service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);
        allowFirstRestoreToComplete.SetResult();

        var manifests = await Task.WhenAll(firstRestoreTask, secondRestoreTask);
        using var firstManifest = manifests[0];
        using var secondManifest = manifests[1];

        Assert.Equal(firstManifest.ManifestPath, secondManifest.ManifestPath);
        Assert.Equal(1, restoreAttemptCount);
        Assert.Equal(1, manifestAttemptCount);
        Assert.Equal(2, invocations.Count);
    }

    [Fact]
    public async Task RestorePackagesAsync_IgnoresLockedLegacyLibsDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        var packageList = new List<(string Id, string Version)> { ("Aspire.Hosting.JavaScript", "9.4.0") };
        var packageHash = BundleNuGetService.ComputePackageHash(packageList, "net10.0", null, managedPath);
        var restoreDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore", packageHash);
        var legacyLibsDirectory = Path.Combine(restoreDirectory, "libs");
        Directory.CreateDirectory(legacyLibsDirectory);
        var lockedFilePath = Path.Combine(legacyLibsDirectory, "Microsoft.Extensions.DependencyInjection.xml");
        File.WriteAllText(lockedFilePath, "legacy");

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        using var lockedFile = new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        using var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(Path.Combine(restoreDirectory, "integration-package-probe-manifest.json"), result.ManifestPath);
        Assert.Equal(2, invocations.Count);
        Assert.DoesNotContain(invocations, args => args.Contains("layout"));
        Assert.Equal("manifest", invocations[1][1]);
    }

    private static string GetArgumentValue(string[] arguments, string optionName)
    {
        var optionIndex = Array.IndexOf(arguments, optionName);
        Assert.True(optionIndex >= 0 && optionIndex < arguments.Length - 1, $"Option '{optionName}' was not found.");
        return arguments[optionIndex + 1];
    }

    private sealed class FixedLayoutDiscovery(LayoutConfiguration layout) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => layout;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => layout.GetComponentPath(component);

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }
}
