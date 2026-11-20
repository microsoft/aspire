// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.DotNet;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSourceProvider = global::NuGet.Configuration.PackageSourceProvider;
using NativeSettingsUtility = global::NuGet.Configuration.SettingsUtility;

namespace Aspire.Cli.Tests.NuGet;

public class BundleNuGetServiceTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ChannelChangesPreserveConfiguredIntegrationFeeds(bool hasMappings, bool stable, bool inherited)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var app = workspace.CreateDirectory("app");
        const string integrationFeed = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-team/nuget/v3/index.json";
        const string selectedFeed = "https://selected.example/v3/index.json";
        File.WriteAllText(Path.Combine(app.FullName, AspireConfigFile.FileName), $$"""
            {
              "integrationSources": {
                "team": {"source":"{{integrationFeed}}","enabled":false}
              }
            }
            """);
        var configPath = Path.Combine(inherited ? workspace.Path : app.FullName, "nuget.config");
        File.WriteAllText(configPath, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="aspire-daily" value="{{integrationFeed}}" />
                <add key="team-alias" value="{{integrationFeed.Replace("pkgs.dev.azure.com", "PKGS.DEV.AZURE.COM", StringComparison.Ordinal)}}" />
                <add key="aspire-staging" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-obsolete/nuget/v3/index.json" />
                <add key="public" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              {{(hasMappings ? """
              <packageSourceMapping>
                <clear />
                <packageSource key="aspire-daily"><package pattern="Aspire.Hosting.Private" /></packageSource>
                <packageSource key="team-alias"><package pattern="Contoso.*" /></packageSource>
                <packageSource key="aspire-staging"><package pattern="Aspire*" /></packageSource>
                <packageSource key="public"><package pattern="*" /></packageSource>
              </packageSourceMapping>
              """ : "")}}
            </configuration>
            """);
        var original = File.ReadAllBytes(configPath);
        var client = NuGetTestHelper.CreateClient();
        var service = new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, client,
            new TestConfigurationService { SettingsFilePath = Path.Combine(workspace.Path, "global.json") });
        var channel = PackageChannel.CreateExplicitChannel(
            stable ? "stable" : "test", PackageChannelQuality.Both,
            [new(stable ? "*" : "Aspire*", stable ? PackageSources.NuGetOrg : selectedFeed)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        var configuration = service.BuildChannelConfiguration(
            app, "test", channel, packageSourceOverride: null, nugetServiceIndexOverride: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(["aspire-daily", "team-alias"],
            configuration.Settings.Sources.Where(static source => source.IsIntegrationSource).Select(static source => source.Name));
        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Equal(["aspire-staging"], overlay.RetiredSourceKeys);
        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Equal(["aspire-staging"], persistence.SourceChanges.RetiredSourceKeys);
        Assert.Empty(persistence.RemovedPackageSourceMappings);

        using var preview = await service.CreateConfigurationPreviewAsync(
            app, configuration, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        var previewSettings = client.GetSettings(preview.EffectiveWorkingDirectory.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        Assert.Equal(stable ? ["aspire-daily", "public", "team-alias"] : ["aspire-daily", "aspire-test", "public", "team-alias"],
            previewSettings.Sources.Where(static source => source.IsEnabled).Select(static source => source.Name).Order(StringComparer.Ordinal));
        if (hasMappings)
        {
            Assert.Equal(["Aspire.Hosting.Private"],
                Assert.Single(previewSettings.PackageSourceMappings, static mapping => mapping.SourceKey == "aspire-daily").Patterns);
        }

        var candidate = await new DotNetAppHostNuGetConfigMerger(service).PrepareAsync(
            app, configuration, createIfMissing: true, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
        var persisted = client.GetSettings(app.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        Assert.Equal(inherited ? ["aspire-daily", "aspire-staging", "public", "team-alias"] : ["aspire-daily", "public", "team-alias"],
            persisted.Sources.Where(static source => source.IsEnabled &&
                source.Name != "aspire-test").Select(static source => source.Name).Order(StringComparer.Ordinal));
        Assert.Equal(hasMappings,
            persisted.PackageSourceMappings.Count > 0);
        if (inherited)
        {
            Assert.Equal(original, File.ReadAllBytes(configPath));
        }
        await Verify(new XElement("configurations",
            new XElement("preview", XDocument.Load(preview.ConfigurationFile!.FullName).Root),
            new XElement("persisted", XDocument.Load(candidate.TargetFile.FullName).Root)).ToString(), "xml")
            .UseParameters(hasMappings, stable, inherited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitSourceOverrideCanReplaceConfiguredIntegrationPolicy(bool invalidConfiguration)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), invalidConfiguration ? """
            {"integrationSources":"invalid"}
            """ : """
            {"integrationSources":{"team":{"source":"https://example.com/team"}}}
            """);
        File.WriteAllText(Path.Combine(workspace.Path, "nuget.config"), """
            <configuration>
              <packageSources><clear /><add key="aspire-daily" value="https://example.com/team" /></packageSources>
              <packageSourceMapping><clear /><packageSource key="aspire-daily"><package pattern="Aspire.Hosting.Private" /></packageSource></packageSourceMapping>
            </configuration>
            """);
        var service = new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, NuGetTestHelper.CreateClient(),
            new TestConfigurationService { SettingsFilePath = Path.Combine(workspace.Path, "global.json") });

        var configuration = service.BuildConfiguration(
            workspace.WorkspaceRoot, "test", [new("Aspire*", "https://example.com/override")],
            restrictToSelectedSources: false, overrideIntegrationSources: true,
            hasAuthoritativeAspirePolicy: true, cancellationToken: TestContext.Current.CancellationToken);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Equal(["aspire-daily"], overlay.RetiredSourceKeys);
        var mapping = Assert.Single(overlay.PackageSourceMappings);
        Assert.Equal("aspire-test", mapping.SourceKey);
        Assert.Equal(["Aspire*"], mapping.Patterns);

        var persistentConfiguration = service.BuildChannelConfiguration(
            workspace.WorkspaceRoot, "test", channel: null,
            packageSourceOverride: "https://example.com/override", nugetServiceIndexOverride: null,
            TestContext.Current.CancellationToken);
        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(persistentConfiguration.AppHostConfig);
        Assert.Equal(["aspire-daily"], persistence.SourceChanges.RetiredSourceKeys);
    }

    [Fact]
    public void IntegrationFeedProtectionIsFreshAndParticipatesInPolicyIdentity()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var global = new FileInfo(Path.Combine(workspace.Path, "global.json"));
        File.WriteAllText(global.FullName, """
            {"integrationSources":{"team":{"source":"https://example.com/team","enabled":false}}}
            """);
        File.WriteAllText(Path.Combine(workspace.Path, "nuget.config"), """
            <configuration>
              <packageSources><clear /><add key="aspire-daily" value="https://example.com/team" /></packageSources>
            </configuration>
            """);
        var service = new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, NuGetTestHelper.CreateClient(),
            new TestConfigurationService { SettingsFilePath = global.FullName });

        var protectedSettings = service.GetNuGetSettings(workspace.Path, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(protectedSettings.Sources).IsIntegrationSource);
        Assert.Equal(protectedSettings.CacheIdentity,
            service.GetNuGetSettings(workspace.Path, TestContext.Current.CancellationToken).CacheIdentity);

        File.WriteAllText(global.FullName, "{}");
        var unprotectedSettings = service.GetNuGetSettings(workspace.Path, TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(unprotectedSettings.Sources).IsIntegrationSource);
        Assert.NotEqual(protectedSettings.CacheIdentity, unprotectedSettings.CacheIdentity);
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        var configuration = DotNetAppHostNuGetConfigBuilder.BuildStable(unprotectedSettings, "test", channel);
        Assert.Equal(["aspire-daily"], configuration.AppHostConfig!.SourceChanges.RetiredSourceKeys);
    }

    [Fact]
    public void ConfiguredLocalIntegrationFeedMatchesResolvedNuGetSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var app = workspace.CreateDirectory("app");
        workspace.CreateDirectory("feed");
        File.WriteAllText(Path.Combine(app.FullName, AspireConfigFile.FileName), """
            {"integrationSources":{"team":{"source":"../feed","enabled":false}}}
            """);
        File.WriteAllText(Path.Combine(workspace.Path, "nuget.config"), """
            <configuration>
              <packageSources><clear /><add key="aspire-daily" value="./feed" /></packageSources>
            </configuration>
            """);
        var service = new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, NuGetTestHelper.CreateClient(),
            new TestConfigurationService { SettingsFilePath = Path.Combine(workspace.Path, "global.json") });

        var settings = service.GetNuGetSettings(app.FullName, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(settings.Sources).IsIntegrationSource);
    }

    [Fact]
    public async Task CreateConfigurationPreviewAsync_PreservesAmbientSettingsWithoutChangingOriginal()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        const string original = """
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="./private-feed" protocolVersion="3" allowInsecureConnections="true" disableTLSCertificateValidation="true" />
                <add key="daily" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="test-user" />
                  <add key="ClearTextPassword" value="test-password" />
                </private>
              </packageSourceCredentials>
              <packageSourceMapping>
                <clear />
                <packageSource key="daily"><package pattern="Aspire*" /></packageSource>
                <packageSource key="private"><package pattern="Contoso.*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
              <config><add key="globalPackagesFolder" value="./packages" /></config>
              <custom><add key="retained" value="unchanged" /></custom>
            </configuration>
            """;
        await File.WriteAllTextAsync(configPath, original);
        var client = NuGetTestHelper.CreateClient();
        var service = new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, client,
            new TestConfigurationService { SettingsFilePath = Path.Combine(workspace.Path, "global.json") });
        var configuration = service.BuildConfiguration(
            appHostDirectory, "test",
            [new PackageMapping("Aspire*", "https://api.nuget.org/v3/index.json"), new PackageMapping("*", "https://api.nuget.org/v3/index.json")],
            restrictToSelectedSources: false, overrideIntegrationSources: false, hasAuthoritativeAspirePolicy: true,
            cancellationToken: TestContext.Current.CancellationToken);
        using var preview = await service.CreateConfigurationPreviewAsync(
            appHostDirectory, configuration, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        Assert.StartsWith(Path.Combine(appHostDirectory.FullName, ".aspire") + Path.DirectorySeparatorChar, preview.EffectiveWorkingDirectory.FullName);
        Assert.Empty(workspace.WorkspaceRoot.EnumerateFiles(".aspire-nuget-preview-*.config"));
        var previewSettings = NuGetTestHelper.LoadSettings(preview.EffectiveWorkingDirectory.FullName);
        var privateSource = new NativeSourceProvider(previewSettings).LoadPackageSources().Single(source => source.Name == "private");
        Assert.Equal(Path.Combine(workspace.WorkspaceRoot.FullName, "private-feed"), privateSource.Source);
        Assert.Equal("test-user", privateSource.Credentials!.Username);
        Assert.Equal("test-password", privateSource.Credentials.Password);

        var key = new byte[NuGetSourceIdentity.KeySizeInBytes];
        var previewSnapshot = client.GetSettings(preview.EffectiveWorkingDirectory.FullName, key);
        Assert.Equal(["private", "nuget.org"],
            previewSnapshot.Sources.Where(static source => source.IsEnabled).Select(static source => source.Name));
        Assert.Equal(["daily"], previewSnapshot.DisabledPackageSourceKeys);
        Assert.Collection(previewSnapshot.PackageSourceMappings,
            mapping =>
            {
                Assert.Equal("private", mapping.SourceKey);
                Assert.Equal(["Contoso.*"], mapping.Patterns);
            },
            mapping =>
            {
                Assert.Equal("nuget.org", mapping.SourceKey);
                Assert.Equal(["*", "Aspire*"], mapping.Patterns);
            });
        var ambientSettings = NuGetTestHelper.LoadSettings(appHostDirectory.FullName);
        Assert.Equal(
            NativeSettingsUtility.GetGlobalPackagesFolder(ambientSettings),
            NativeSettingsUtility.GetGlobalPackagesFolder(previewSettings));
        Assert.Equal(
            ambientSettings.GetSection("packageSources")!.Items.OfType<global::NuGet.Configuration.SourceItem>().Single(source => source.Key == "private").AdditionalAttributes,
            previewSettings.GetSection("packageSources")!.Items.OfType<global::NuGet.Configuration.SourceItem>().Single(source => source.Key == "private").AdditionalAttributes);
        Assert.Equal("unchanged", previewSettings.GetSection("custom")!.Items.OfType<global::NuGet.Configuration.AddItem>().Single().Value);
    }

    [Fact]
    public async Task CreateConfigurationPreviewAsync_PreservesParentConfigWhenCreatingLocalFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="app" value="https://app.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="app"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var client = NuGetTestHelper.CreateClient();
        var service = new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, client,
            new TestConfigurationService { SettingsFilePath = Path.Combine(workspace.Path, "global.json") });
        var configuration = service.BuildAppHostConfiguration(
            appHostDirectory, "test",
            [new PackageMapping("Aspire*", "https://selected.example/v3/index.json")],
            overrideIntegrationSources: false,
            cancellationToken: TestContext.Current.CancellationToken);
        using var preview = await service.CreateConfigurationPreviewAsync(
            appHostDirectory, configuration, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        var candidate = await new DotNetAppHostNuGetConfigMerger(service).PrepareAsync(
            appHostDirectory, configuration, createIfMissing: true,
            globalPackagesFolder: null, TestContext.Current.CancellationToken);
        Assert.NotNull(candidate);

        Assert.False(candidate.TargetFile.Exists);
        var previewSnapshot = client.GetSettings(preview.EffectiveWorkingDirectory.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        Assert.Equal(["app", "aspire-test"], previewSnapshot.Sources.Select(source => source.Name).Order());
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
        var persistedSnapshot = client.GetSettings(appHostDirectory.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        Assert.Equal(
            persistedSnapshot.Sources.OrderBy(static source => source.Name),
            previewSnapshot.Sources.OrderBy(static source => source.Name));
    }

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
        Assert.Equal(0, nuGetClient.GetSettingsCallCount);
        Assert.Equal(0, nuGetClient.WriteNuGetConfigCallCount);
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
            nuGetClient,
            new TestConfigurationService());

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
