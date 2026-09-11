// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Reflection;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
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

        var manifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

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

        var manifestPathA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-a/index.json"],
            workingDirectory: appHostDirectory.FullName);

        var manifestPathB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/feed-b/index.json"],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(manifestPathA, manifestPathB);
    }

    [Fact]
    public async Task RestorePackagesAsync_ReusesCacheForEquivalentTemporaryOverlays()
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
        const string configContent = """
            <configuration>
              <packageSourceMapping>
                <packageSource key="shared"><package pattern="Aspire.*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        await File.WriteAllTextAsync(firstConfigPath, configContent);
        await File.WriteAllTextAsync(secondConfigPath, configContent);

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        var manifestPathA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            nugetConfigPaths: [firstConfigPath],
            nugetSettingsCacheIdentity: "ambient-settings",
            nugetConfigOverlayCacheIdentity: "shared-overlay",
            workingDirectory: appHostDirectory.FullName);
        var manifestPathB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            nugetConfigPaths: [secondConfigPath],
            nugetSettingsCacheIdentity: "ambient-settings",
            nugetConfigOverlayCacheIdentity: "shared-overlay",
            workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPathA, manifestPathB);
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

        var manifestPathA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);
        environmentVariables[CliPathHelper.NuGetPackagesEnvironmentVariable] =
            Path.Combine(workspace.WorkspaceRoot.FullName, "packages-b");
        var manifestPathB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(manifestPathA, manifestPathB);
    }

    [Fact]
    public async Task RestorePackagesAsync_UsesDistinctCachePathsWhenEffectiveSettingsChange()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(
            configPath,
            """
            <configuration>
              <packageSources>
                <add key="environment" value="$ASPIRE_TEST_PACKAGE_SOURCE" />
              </packageSources>
            </configuration>
            """);
        var environmentVariables = new Dictionary<string, string?>
        {
            ["ASPIRE_TEST_PACKAGE_SOURCE"] = "https://example.com/feed-a"
        };
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(environmentVariables),
            NullLogger<BundleNuGetService>.Instance);

        var manifestPathA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            nugetConfigPaths: [configPath],
            nugetSettingsCacheIdentity: "feed-a",
            nugetConfigOverlayCacheIdentity: "environment-overlay",
            workingDirectory: appHostDirectory.FullName);
        environmentVariables["ASPIRE_TEST_PACKAGE_SOURCE"] = "https://example.com/feed-b";
        var manifestPathB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            nugetConfigPaths: [configPath],
            nugetSettingsCacheIdentity: "feed-b",
            nugetConfigOverlayCacheIdentity: "environment-overlay",
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(manifestPathA, manifestPathB);
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

        var manifestPathA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);
        environmentVariables[CliPathHelper.NuGetFallbackPackagesEnvironmentVariable] = $"{fallbackB};{fallbackA}";
        var manifestPathB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(manifestPathA, manifestPathB);
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

        var firstManifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            globalPackagesFolderOverride: stagingPackagesFolder);
        environmentVariables[CliPathHelper.NuGetPackagesEnvironmentVariable] =
            Path.Combine(workspace.WorkspaceRoot.FullName, "different-inherited-packages");
        var secondManifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            globalPackagesFolderOverride: stagingPackagesFolder);

        Assert.Equal(firstManifestPath, secondManifestPath);
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

        var manifestPathA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/shared/index.json"],
            nugetConfigPaths: [firstConfigPath],
            nugetSettingsCacheIdentity: "first-settings",
            workingDirectory: appHostDirectory.FullName);
        var manifestPathB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://example.com/shared/index.json"],
            nugetConfigPaths: [secondConfigPath],
            nugetSettingsCacheIdentity: "second-settings",
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(manifestPathA, manifestPathB);
    }

    [Fact]
    public async Task RestorePackagesAsync_ReusesCacheForUnchangedCredentialBearingNuGetConfig()
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

        var firstManifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            nugetConfigPaths: [configPath],
            nugetSettingsCacheIdentity: "settings",
            workingDirectory: appHostDirectory.FullName);
        var secondManifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            nugetConfigPaths: [configPath],
            nugetSettingsCacheIdentity: "settings",
            workingDirectory: appHostDirectory.FullName);

        Assert.Equal(firstManifestPath, secondManifestPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_ReusesCacheForUnchangedCredentialBearingSources()
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

        var firstManifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: [credentialBearingSource],
            workingDirectory: appHostDirectory.FullName);
        var secondManifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: [credentialBearingSource],
            workingDirectory: appHostDirectory.FullName);

        Assert.Equal(firstManifestPath, secondManifestPath);
    }

    [Fact]
    public async Task RestorePackagesAsync_CredentialBearingSourceUsesConfiguredGlobalPackagesFolder()
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

        await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: [credentialBearingSource],
            workingDirectory: appHostDirectory.FullName,
            globalPackagesFolderOverride: persistentPackagesFolder);

        Assert.Equal(
            persistentPackagesFolder,
            executionFactory.LastEnvironmentVariables?[CliPathHelper.NuGetPackagesEnvironmentVariable]);
    }

    [Fact]
    public async Task RestorePackagesAsync_DoesNotManageLegacyTemporaryRestoreDirectories()
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
            "temporary");
        var abandonedDirectory = Path.Combine(
            temporaryRoot,
            $".credential-{Guid.NewGuid():N}");
        Directory.CreateDirectory(abandonedDirectory);
        File.WriteAllText(Path.Combine(abandonedDirectory, "project.assets.json"), "credential-bearing restore metadata");

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        var manifestPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            sources: ["https://packages.example.com/v3/index.json?sig=secret"],
            workingDirectory: appHostDirectory.FullName);

        Assert.True(Directory.Exists(abandonedDirectory));
        Assert.True(Directory.Exists(Directory.GetParent(manifestPath)!.FullName));
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
            workingDirectory: appHostDirectory.FullName,
            additionalSensitiveSources: [credentialBearingSource]));

        Assert.DoesNotContain(credentialBearingSource, exception.Message);
        Assert.Contains("packages.example.com", exception.Message);
        Assert.DoesNotContain(sink.Writes, write => write.Message?.Contains(credentialBearingSource, StringComparison.Ordinal) == true);
        Assert.NotNull(restoreOutputPath);
    }

    [Fact]
    public async Task GetNuGetSettingsAsync_UsesBundledHelper()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var configPath = Path.Combine(appHostDirectory.FullName, "NuGet.Config");
        const string packageSource = "https://example.com/feed";
        var sourceIdentityKey = new byte[NuGetSourceIdentity.KeySizeInBytes];
        string[]? invocation = null;
        IDictionary<string, string>? invocationEnvironment = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, environment, _, _) =>
            {
                invocation = args;
                invocationEnvironment = environment;
            },
            AttemptCallback = (_, _) => (0, System.Text.Json.JsonSerializer.Serialize(new
            {
                ConfigPaths = new[] { configPath },
                CacheIdentity = "settings",
                Sources = new[]
                {
                    new
                    {
                        Name = "private",
                        Identity = NuGetSourceIdentity.Compute(packageSource, sourceIdentityKey),
                        IsEnabled = true,
                        HasCredentials = false,
                        HasClientCertificates = false
                    }
                },
                SensitiveSourceValues = Array.Empty<string>(),
                PackageSourceMappingEnabled = true,
                PackageSourceMappings = new[]
                {
                    new { SourceKey = "private", Patterns = new[] { "Aspire*" } }
                },
                DisabledPackageSourceKeys = new[] { "disabled" },
                ReservedPackageSourceKeys = new[] { "private", "disabled", "credentials-only" }
            }))
        };
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance)
        {
            SourceIdentityKeyFactory = () => sourceIdentityKey
        };

        var settings = await service.GetNuGetSettingsAsync(appHostDirectory.FullName, CancellationToken.None);

        Assert.Equal([configPath], settings.ConfigPaths);
        Assert.Equal("settings", settings.CacheIdentity);
        var source = Assert.Single(settings.Sources);
        Assert.Equal(
            new NuGetSourceInfo(
                "private",
                NuGetSourceIdentity.Compute(packageSource, sourceIdentityKey),
                IsEnabled: true,
                HasCredentials: false,
                HasClientCertificates: false),
            source);
        Assert.Empty(settings.SensitiveSourceValues);
        Assert.Same(sourceIdentityKey, settings.SourceIdentityKey);
        Assert.True(settings.PackageSourceMappingEnabled);
        var mapping = Assert.Single(settings.PackageSourceMappings);
        Assert.Equal("private", mapping.SourceKey);
        Assert.Equal(["Aspire*"], mapping.Patterns);
        Assert.Equal(["disabled"], settings.DisabledPackageSourceKeys);
        Assert.Equal(["private", "disabled", "credentials-only"], settings.ReservedPackageSourceKeys);
        Assert.Equal(["nuget", "settings", "--working-dir", appHostDirectory.FullName], invocation!);
        Assert.Equal(
            Convert.ToBase64String(sourceIdentityKey),
            invocationEnvironment![NuGetSourceIdentity.KeyEnvironmentVariable]);
    }

    [Fact]
    public async Task GetNuGetSettingsAsync_ParsesActualManagedSettingsOutput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var localSourceDirectory = workspace.CreateDirectory("local-source");
        var marker = $"redaction-marker-{Guid.NewGuid():N}";
        var sensitiveSource = CreateSensitiveSource(marker);
        var configPath = Path.Combine(appHostDirectory.FullName, "NuGet.Config");
        File.WriteAllText(
            configPath,
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{localSourceDirectory.FullName}" />
                <add key="sensitive" value="{sensitiveSource}" />
              </packageSources>
              <disabledPackageSources>
                <add key="local" value="false" />
              </disabledPackageSources>
              <packageSourceMapping>
                <packageSource key="local">
                  <package pattern="Contoso.*" />
                </packageSource>
                <packageSource key="sensitive">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var sourceIdentityKey = new byte[NuGetSourceIdentity.KeySizeInBytes];
        var service = CreateServiceWithActualManagedHelper(
            NullLogger<BundleNuGetService>.Instance,
            sourceIdentityKey);

        var settings = await service.GetNuGetSettingsAsync(
            appHostDirectory.FullName,
            TestContext.Current.CancellationToken);

        Assert.Contains(configPath, settings.ConfigPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            settings.Sources,
            source => source.Name == "local" &&
                source.Identity == NuGetSourceIdentity.Compute(localSourceDirectory.FullName, sourceIdentityKey) &&
                !source.IsEnabled &&
                !source.HasCredentials &&
                !source.HasClientCertificates);
        Assert.Contains(
            settings.Sources,
            source => source.Name == "sensitive" &&
                source.Identity == NuGetSourceIdentity.Compute(sensitiveSource, sourceIdentityKey) &&
                source.IsEnabled &&
                !source.HasCredentials &&
                !source.HasClientCertificates);
        Assert.Equal([sensitiveSource], settings.SensitiveSourceValues);
        Assert.True(settings.PackageSourceMappingEnabled);
        Assert.Contains(
            settings.PackageSourceMappings,
            mapping => mapping.SourceKey == "local" && mapping.Patterns.SequenceEqual(["Contoso.*"]));
        Assert.Contains(
            settings.PackageSourceMappings,
            mapping => mapping.SourceKey == "sensitive" && mapping.Patterns.SequenceEqual(["Aspire*"]));
        Assert.Contains("local", settings.DisabledPackageSourceKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("local", settings.ReservedPackageSourceKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sensitive", settings.ReservedPackageSourceKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Same(sourceIdentityKey, settings.SourceIdentityKey);
    }

    [Fact]
    public async Task RestorePackagesAsync_RedactsAmbientSourceReportedByActualManagedHelper()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var marker = $"redaction-marker-{Guid.NewGuid():N}";
        var sensitiveSource = CreateSensitiveSource(marker);
        File.WriteAllText(
            Path.Combine(appHostDirectory.FullName, "NuGet.Config"),
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="unavailable" value="{sensitiveSource}" />
              </packageSources>
            </configuration>
            """);
        var sink = new TestSink();
        var logger = new TestLogger<BundleNuGetService>(new TestLoggerFactory(sink, enabled: true));
        var service = CreateServiceWithActualManagedHelper(
            logger,
            new byte[NuGetSourceIdentity.KeySizeInBytes]);
        var settings = await service.GetNuGetSettingsAsync(
            appHostDirectory.FullName,
            TestContext.Current.CancellationToken);
        Assert.Equal([sensitiveSource], settings.SensitiveSourceValues);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestorePackagesAsync(
            [("Aspire.RedactionProbe.DoesNotExist", "0.0.0")],
            workingDirectory: appHostDirectory.FullName,
            nugetConfigPaths: settings.ConfigPaths,
            nugetSettingsCacheIdentity: settings.CacheIdentity,
            additionalSensitiveSources: settings.SensitiveSourceValues,
            ct: timeout.Token));

        var redactedSource = PackageSourceRedactor.RedactForDisplay(sensitiveSource);
        Assert.Contains(redactedSource, exception.Message);
        Assert.DoesNotContain(marker, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveSource, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            sink.Writes,
            write => write.Message?.Contains(redactedSource, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            sink.Writes,
            write => write.Message?.Contains(marker, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task GetNuGetSettingsAsync_ReturnsAuditSourceForExactRedaction()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var marker = $"audit-redaction-marker-{Guid.NewGuid():N}";
        var sensitiveAuditSource = CreateSensitiveSource(marker);
        File.WriteAllText(
            Path.Combine(appHostDirectory.FullName, "NuGet.Config"),
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="packages" value="https://packages.example.invalid/v3/index.json" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="audit" value="{sensitiveAuditSource}" />
              </auditSources>
            </configuration>
            """);
        var service = CreateServiceWithActualManagedHelper(
            NullLogger<BundleNuGetService>.Instance,
            new byte[NuGetSourceIdentity.KeySizeInBytes]);

        var settings = await service.GetNuGetSettingsAsync(
            appHostDirectory.FullName,
            TestContext.Current.CancellationToken);
        var diagnostic = $"warning NU1900: Error occurred while getting package vulnerability data: {sensitiveAuditSource}";
        var redactedDiagnostic = PackageSourceRedactor.RedactOccurrences(
            diagnostic,
            settings.SensitiveSourceValues);

        Assert.Equal([sensitiveAuditSource], settings.SensitiveSourceValues);
        Assert.Contains(PackageSourceRedactor.RedactForDisplay(sensitiveAuditSource), redactedDiagnostic);
        Assert.DoesNotContain(marker, redactedDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveAuditSource, redactedDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteNuGetConfigOverlayAsync_UsesBundledHelperAndDeletesRequest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        string[]? invocation = null;
        string? requestPath = null;
        string? requestJson = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) =>
            {
                invocation = args;
                requestPath = GetArgumentValue(args, "--request");
                requestJson = File.ReadAllText(requestPath);
                File.WriteAllText(GetArgumentValue(args, "--output"), "<configuration />");
            }
        };
        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);
        var overlay = new NuGetConfigOverlayInfo(
            [new NuGetConfigSourceDefinition("aspire-0", "https://example.com/feed")],
            [new NuGetPackageSourceMappingInfo("aspire-0", ["Aspire*"])],
            ClearDisabledPackageSources: true,
            DisabledPackageSourceKeys: ["unrelated"],
            GlobalPackagesFolder: "/packages");

        await service.WriteNuGetConfigOverlayAsync(overlay, outputPath, CancellationToken.None);

        Assert.Equal(
            ["nuget", "write-config", "--request", requestPath!, "--output", outputPath],
            invocation!);
        Assert.NotNull(requestJson);
        using var request = System.Text.Json.JsonDocument.Parse(requestJson);
        var source = request.RootElement.GetProperty("Sources").EnumerateArray().Single();
        var mapping = request.RootElement.GetProperty("PackageSourceMappings").EnumerateArray().Single();
        Assert.Equal("aspire-0", source.GetProperty("Key").GetString());
        Assert.Equal("Aspire*", mapping.GetProperty("Patterns").EnumerateArray().Single().GetString());
        Assert.Equal("/packages", request.RootElement.GetProperty("GlobalPackagesFolder").GetString());
        Assert.True(File.Exists(outputPath));
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public void ComputePackageHash_DistinguishesGlobalPackagesPathFromAppendedFallbackPath()
    {
        var packages = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };

        var embeddedFallbackHash = BundleNuGetService.ComputePackageHash(
            packages,
            "net10.0",
            runtimeIdentifier: null,
            nugetPackagesPath: "/x;fallback-packages:2:/y",
            nugetFallbackPackagesPaths: []);
        var separateFallbackHash = BundleNuGetService.ComputePackageHash(
            packages,
            "net10.0",
            runtimeIdentifier: null,
            nugetPackagesPath: "/x",
            nugetFallbackPackagesPaths: ["/y"]);

        Assert.NotEqual(embeddedFallbackHash, separateFallbackHash);
    }

    [Fact]
    public void ComputePackageHash_DistinguishesSourceListsContainingDelimiters()
    {
        var packages = new List<(string Id, string Version)>
        {
            ("Aspire.Hosting.JavaScript", "9.4.0")
        };

        var delimiterInFirstSourceHash = BundleNuGetService.ComputePackageHash(
            packages,
            "net10.0",
            runtimeIdentifier: null,
            sources: ["/a|/b", "/c"]);
        var delimiterInSecondSourceHash = BundleNuGetService.ComputePackageHash(
            packages,
            "net10.0",
            runtimeIdentifier: null,
            sources: ["/a", "/b|/c"]);

        Assert.NotEqual(delimiterInFirstSourceHash, delimiterInSecondSourceHash);
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

        await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName,
            nugetConfigPaths: [nugetConfigPath],
            nugetSettingsCacheIdentity: "settings");

        Assert.Contains("--no-nuget-org", invocations[0]);
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

        var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result);
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

        var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result);
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

        var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(manifestPath, result);
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

        var manifestPathA = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        File.WriteAllText(managedPath, "v2-changed");

        var manifestPathB = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        Assert.NotEqual(manifestPathA, manifestPathB);
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
        var sharedManifestFirst = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: firstAppHost.FullName);
        var sharedManifestSecond = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, sharedManifestFirst, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(restoreRoot, sharedManifestSecond, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sharedManifestFirst, sharedManifestSecond);

        // Different package sets must NOT collide even when workspace is shared.
        var divergedManifest = await service.RestorePackagesAsync(
            [("Aspire.Hosting.Python", "9.4.0")],
            workingDirectory: secondAppHost.FullName);

        Assert.StartsWith(restoreRoot, divergedManifest, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(sharedManifestSecond, divergedManifest);
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

        Assert.Equal(manifests[0], manifests[1]);
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

        var result = await service.RestorePackagesAsync(packageList, workingDirectory: appHostDirectory.FullName);

        Assert.Equal(Path.Combine(restoreDirectory, "integration-package-probe-manifest.json"), result);
        Assert.Equal(2, invocations.Count);
        Assert.DoesNotContain(invocations, args => args.Contains("layout"));
        Assert.Equal("manifest", invocations[1][1]);
    }

    private static BundleNuGetService CreateServiceWithActualManagedHelper(
        ILogger<BundleNuGetService> logger,
        byte[] sourceIdentityKey)
    {
        var managedPath = GetBuiltManagedPath();
        var environment = new TestEnvironment();
        var layout = new LayoutConfiguration
        {
            LayoutPath = Path.GetDirectoryName(managedPath),
            Components = new LayoutComponents { Managed = "." }
        };

        return new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(
                new ProcessExecutionFactory(
                    environment,
                    NullLogger<ProcessExecutionFactory>.Instance)),
            new TestFeatures(),
            environment,
            logger)
        {
            SourceIdentityKeyFactory = () => sourceIdentityKey
        };
    }

    private static string CreateSensitiveSource(string marker)
        => new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", 1, "v3/index.json")
        {
            Query = $"opaque={Uri.EscapeDataString(marker)}"
        }.Uri.AbsoluteUri;

    private static string GetBuiltManagedPath()
    {
        var configuration = typeof(BundleNuGetServiceTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration
            ?? throw new InvalidOperationException("The test assembly build configuration was unavailable.");
        var repoRoot = FindRepoRoot();
        var managedPath = Path.Combine(
            repoRoot,
            "artifacts",
            "bin",
            "Aspire.Managed",
            configuration,
            "net10.0",
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));

        Assert.True(File.Exists(managedPath), $"The built aspire-managed executable was not found at '{managedPath}'.");
        return managedPath;
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Unable to find the repository root.");
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
