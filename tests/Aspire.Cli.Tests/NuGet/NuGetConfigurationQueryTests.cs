// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.DotNet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetConfigurationQueryTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, "Contoso.*")]
    [InlineData(true, "Contoso.*")]
    [InlineData(false, "Aspire.Hosting.Private")]
    public async Task StableChannelPolicy_RejectsAmbiguousPrivateDependencyRouting(bool separateAlias, string narrowPattern)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("AppHost");
        const string privateFeed = "https://example.test/team/v3/index.json";
        var configPath = Path.Combine(workspace.Path, "nuget.config");
        var original = $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="aspire-daily" value="{{privateFeed}}" />
                {{(separateAlias ? $"""<add key="company" value="{privateFeed}" />""" : "")}}
                <add key="public" value="{{PackageSources.NuGetOrg}}" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="aspire-daily">
                  <package pattern="Aspire*" />
                  {{(separateAlias ? "" : $"""<package pattern="{narrowPattern}" />""")}}
                </packageSource>
                {{(separateAlias ? $"""<packageSource key="company"><package pattern="{narrowPattern}" /></packageSource>""" : "")}}
                <packageSource key="public"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        await File.WriteAllTextAsync(configPath, original);
        await File.WriteAllTextAsync(Path.Combine(appHostDirectory.FullName, Aspire.Cli.Configuration.AspireConfigFile.FileName), $$"""
            {
              "integrationSources": {
                "default": {"enabled":false},
                "team": {"source":"{{privateFeed}}","patterns":["Contoso.*"]}
              }
            }
            """);
        var service = new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, NuGetTestHelper.CreateClient(),
            new TestConfigurationService { SettingsFilePath = Path.Combine(workspace.Path, "global.json") });
        var settings = service.GetNuGetSettings(appHostDirectory.FullName, TestContext.Current.CancellationToken);
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        Assert.Equal([privateFeed], NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Private.Dependency"));
        Assert.Throws<ProjectUpdaterException>(() =>
            DotNetAppHostNuGetConfigBuilder.BuildStable(settings, "test", channel));
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        Assert.False(File.Exists(Path.Combine(appHostDirectory.FullName, "nuget.config")));
        Assert.Equal([privateFeed], NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Private.Dependency"));
    }

    [Theory]
    [InlineData("Aspire.Hosting.Private", false, true)]
    [InlineData("Aspire.Hosting.Private*", false, true)]
    [InlineData("Aspire.Hosting.Private", true, true)]
    [InlineData("Aspire.Hosting.Private", false, false)]
    [InlineData("*", false, true)]
    public async Task StableChannelPolicy_PreservesIntegrationFeedAndRouting(
        string privatePattern, bool separateIntegrationAlias, bool isEnabled)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("AppHost");
        const string privateFeed = "https://example.test/team/v3/index.json";
        const string sdkSourceKey = "aspire-daily";
        var integrationKey = separateIntegrationAlias ? "company" : sdkSourceKey;
        var configPath = Path.Combine(workspace.Path, "nuget.config");
        var original = $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="{{sdkSourceKey}}" value="{{privateFeed}}" />
                <add key="aspire-staging" value="https://example.test/sdk/v3/index.json" />
                {{(separateIntegrationAlias ? $"""<add key="{integrationKey}" value="{privateFeed}" />""" : "")}}
                <add key="public" value="{{PackageSources.NuGetOrg}}" />
              </packageSources>
              <packageSourceCredentials>
                <aspire-daily><add key="Username" value="test-user" /><add key="ClearTextPassword" value="test-password" /></aspire-daily>
              </packageSourceCredentials>
              {{(isEnabled ? "" : """<disabledPackageSources><add key="aspire-daily" value="true" /></disabledPackageSources>""")}}
              <packageSourceMapping>
                <clear />
                <packageSource key="aspire-staging"><package pattern="Aspire*" /></packageSource>
                {{(separateIntegrationAlias ? "" : $"""<packageSource key="{sdkSourceKey}"><package pattern="{privatePattern}" /></packageSource>""")}}
                {{(separateIntegrationAlias ? $"""<packageSource key="{integrationKey}"><package pattern="{privatePattern}" /></packageSource>""" : "")}}
                <packageSource key="public"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        await File.WriteAllTextAsync(configPath, original);
        await File.WriteAllTextAsync(Path.Combine(appHostDirectory.FullName, Aspire.Cli.Configuration.AspireConfigFile.FileName), $$"""
            {
              "integrationSources": {
                "default": {"enabled":false},
                "team": {"source":"{{privateFeed}}","patterns":["Aspire.Hosting.Private"]}
              }
            }
            """);
        var service = new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, NuGetTestHelper.CreateClient(),
            new TestConfigurationService { SettingsFilePath = Path.Combine(workspace.Path, "global.json") });
        var settings = service.GetNuGetSettings(appHostDirectory.FullName, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(settings.Sources, source => source.Name == sdkSourceKey).IsIntegrationSource);
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        if (privatePattern == PackageMapping.AllPackages)
        {
            Assert.Throws<ProjectUpdaterException>(() =>
                DotNetAppHostNuGetConfigBuilder.BuildStable(settings, "test", channel));
            Assert.Equal(original, await File.ReadAllTextAsync(configPath));
            Assert.False(File.Exists(Path.Combine(appHostDirectory.FullName, "nuget.config")));
            return;
        }

        var configuration = DotNetAppHostNuGetConfigBuilder.BuildStable(settings, "test", channel);
        using var preview = await service.CreateConfigurationPreviewAsync(
            appHostDirectory, configuration, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        var previewDirectory = preview.EffectiveWorkingDirectory.FullName;
        string[] privateSources = isEnabled ? [privateFeed] : [];

        Assert.Equal([PackageSources.NuGetOrg], NuGetTestHelper.GetEligiblePackageSources(previewDirectory, "Aspire.AppHost.Sdk"));
        Assert.Equal([PackageSources.NuGetOrg], NuGetTestHelper.GetEligiblePackageSources(previewDirectory, "Aspire.Hosting.AppHost"));
        Assert.Equal(privateSources, NuGetTestHelper.GetEligiblePackageSources(previewDirectory, "Aspire.Hosting.Private"));
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        var merger = new DotNetAppHostNuGetConfigMerger(service);
        var candidate = await merger.PrepareAsync(
            workspace.WorkspaceRoot, configuration, createIfMissing: false, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);

        Assert.Equal([PackageSources.NuGetOrg], NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.AppHost.Sdk"));
        Assert.Equal([PackageSources.NuGetOrg], NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Hosting.AppHost"));
        Assert.Equal(privateSources, NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Hosting.Private"));
        var retained = Assert.Single(service.GetNuGetSettings(appHostDirectory.FullName, TestContext.Current.CancellationToken).Sources,
            source => source.Name == sdkSourceKey);
        Assert.Equal(isEnabled, retained.IsEnabled);
        Assert.True(retained.HasCredentials);
        var persistedSource = Assert.Single(new global::NuGet.Configuration.PackageSourceProvider(
            NuGetTestHelper.LoadSettings(appHostDirectory.FullName)).LoadPackageSources(), source => source.Name == sdkSourceKey);
        Assert.NotNull(persistedSource.Credentials);
        Assert.Equal("test-user", persistedSource.Credentials.Username);
        Assert.Equal("test-password", persistedSource.Credentials.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("Aspire*")]
    public async Task ExactIntegrationSelection_PreservesSdkFeedAfterPersistence(string? sdkPattern)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var sdkFeed = workspace.CreateDirectory(Path.Combine(".aspire", "hives", "pr-21366", "packages")).FullName;
        var integrationFeed = workspace.CreateDirectory("integration-feed").FullName;
        const string sdkSourceKey = "aspire-pr-21366";
        const string packageId = "Contoso.Hosting.One";
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "nuget.config"), $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="{{sdkSourceKey}}" value="{{sdkFeed}}" />
                <add key="public" value="{{PackageSources.NuGetOrg}}" />
              </packageSources>
              {{(sdkPattern is null ? "" : $"""
              <packageSourceMapping>
                <packageSource key="{sdkSourceKey}"><package pattern="{sdkPattern}" /></packageSource>
                <packageSource key="public"><package pattern="*" /></packageSource>
              </packageSourceMapping>
              """)}}
            </configuration>
            """);
        var service = NuGetTestHelper.CreateService();
        Assert.True(Assert.Single(service.GetNuGetSettings(workspace.Path, TestContext.Current.CancellationToken).Sources,
            source => source.Name == sdkSourceKey).IsCliManaged);
        var configuration = service.BuildAppHostConfiguration(
            workspace.WorkspaceRoot, "integration-add",
            [new(packageId, integrationFeed) { SourceKey = "team" }],
            overrideIntegrationSources: false, TestContext.Current.CancellationToken);
        var candidate = await new DotNetAppHostNuGetConfigMerger(service).PrepareAsync(
            workspace.WorkspaceRoot, configuration, createIfMissing: false,
            globalPackagesFolder: null, TestContext.Current.CancellationToken);
        Assert.NotNull(candidate);

        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(service.GetNuGetSettings(workspace.Path, TestContext.Current.CancellationToken).Sources,
            source => source.Name == sdkSourceKey).IsEnabled);
        Assert.Equal(sdkPattern is not null,
            service.IsPackageSourceMappingEnabled(workspace.WorkspaceRoot, TestContext.Current.CancellationToken));
        string[] allSources = [sdkFeed, PackageSources.NuGetOrg, integrationFeed];
        string[] sdkSources = sdkPattern switch
        {
            null => allSources,
            "*" => [sdkFeed, PackageSources.NuGetOrg],
            _ => [sdkFeed]
        };
        Assert.Equal(sdkSources.Order(StringComparer.Ordinal),
            NuGetTestHelper.GetEligiblePackageSources(workspace.Path, "Aspire.Hosting.AppHost"));
        Assert.Equal((sdkPattern is null ? allSources : [integrationFeed]).Order(StringComparer.Ordinal),
            NuGetTestHelper.GetEligiblePackageSources(workspace.Path, packageId));
    }

    [Theory]
    [InlineData(PackageChannelNames.Daily, false)]
    [InlineData(PackageChannelNames.Daily, true)]
    [InlineData(PackageChannelNames.Staging, false)]
    [InlineData(PackageChannelNames.Staging, true)]
    [InlineData(PackageChannelNames.Stable, false)]
    [InlineData(PackageChannelNames.Stable, true)]
    public async Task ChannelPolicy_PreservesAmbientFeedsAndRetiresOwnedSources(string channelName, bool hasMappings)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("AppHost");
        var oldHive = workspace.CreateDirectory(".aspire/hives/pr-old/packages").FullName;
        const string customerSource = "https://pkgs.dev.azure.com/contoso/team/_packaging/aspire-company/nuget/v3/index.json";
        const string privateSource = "https://packages.example.com/v3/index.json";
        const string sharedSource = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json";
        const string oldStagingSource = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json";
        const string newStagingSource = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-12345678/nuget/v3/index.json";
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        var original = $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="customer" value="{{customerSource}}" />
                <add key="private" value="{{privateSource}}" />
                <add key="shared" value="{{sharedSource}}" />
                <add key="public" value="{{PackageSources.NuGetOrg}}" />
                <add key="old-staging" value="{{oldStagingSource}}" />
                <add key="old-hive" value="{{oldHive}}" />
              </packageSources>
              {{(hasMappings ? """
              <packageSourceMapping>
                <packageSource key="customer"><package pattern="*" /></packageSource>
                <packageSource key="private"><package pattern="*" /></packageSource>
                <packageSource key="shared"><package pattern="*" /></packageSource>
                <packageSource key="public"><package pattern="*" /></packageSource>
                <packageSource key="old-staging"><package pattern="*" /></packageSource>
                <packageSource key="old-hive"><package pattern="*" /></packageSource>
              </packageSourceMapping>
              """ : "")}}
            </configuration>
            """;
        await File.WriteAllTextAsync(configPath, original);
        var service = NuGetTestHelper.CreateService();
        var selectedSource = channelName switch
        {
            PackageChannelNames.Daily => sharedSource,
            PackageChannelNames.Staging => newStagingSource,
            _ => PackageSources.NuGetOrg
        };
        var channel = PackageChannel.CreateExplicitChannel(
            channelName, channelName == PackageChannelNames.Stable ? PackageChannelQuality.Stable : PackageChannelQuality.Both,
            channelName == PackageChannelNames.Stable
                ? [new PackageMapping("*", PackageSources.NuGetOrg)]
                : [new PackageMapping("Aspire*", selectedSource), new PackageMapping("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        var configuration = channelName == PackageChannelNames.Stable
            ? DotNetAppHostNuGetConfigBuilder.BuildStable(
                service.GetNuGetSettings(appHostDirectory.FullName, TestContext.Current.CancellationToken),
                "test", channel)
            : service.BuildChannelConfiguration(
                appHostDirectory, "test", channel, packageSourceOverride: null,
                nugetServiceIndexOverride: null, TestContext.Current.CancellationToken);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Equal(["old-hive", "old-staging"], overlay.RetiredSourceKeys.Order(StringComparer.Ordinal));
        using var preview = await service.CreateConfigurationPreviewAsync(
            appHostDirectory, configuration, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        var ambientSources = new[] { customerSource, privateSource, sharedSource, PackageSources.NuGetOrg }
            .Order(StringComparer.Ordinal).ToArray();
        string[] aspireSources = channelName == PackageChannelNames.Stable ? ambientSources : [selectedSource];
        var persistedSources = ambientSources.Append(selectedSource).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var previewDirectory = preview.EffectiveWorkingDirectory.FullName;

        Assert.Equal(ambientSources, NuGetTestHelper.GetEligiblePackageSources(previewDirectory, "Contoso.Package"));
        Assert.Equal(aspireSources, NuGetTestHelper.GetEligiblePackageSources(previewDirectory, "Aspire.Hosting"));
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));

        var merger = new DotNetAppHostNuGetConfigMerger(service);
        if (channelName == PackageChannelNames.Daily && hasMappings)
        {
            await Assert.ThrowsAsync<Aspire.Cli.Projects.ProjectUpdaterException>(() =>
                merger.PrepareAsync(workspace.WorkspaceRoot, configuration, createIfMissing: false,
                    globalPackagesFolder: null, TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllTextAsync(configPath));
            return;
        }

        var candidate = await merger.PrepareAsync(
            workspace.WorkspaceRoot, configuration, createIfMissing: false,
            globalPackagesFolder: null, TestContext.Current.CancellationToken);
        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);

        Assert.Equal(hasMappings ? ambientSources : persistedSources,
            NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Contoso.Package"));
        Assert.Equal(hasMappings ? aspireSources : persistedSources,
            NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Hosting"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelPolicy_RetiresUnselectedHivesBeforePersistence(bool hasMappings)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("AppHost");
        var oldHive = workspace.CreateDirectory(".aspire/hives/pr-old/packages").FullName;
        var newHive = workspace.CreateDirectory(".aspire/hives/pr-new/packages").FullName;
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        var original = $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="old-hive" value="{{oldHive}}" />
                <add key="private" value="https://private.example/v3/index.json" />
              </packageSources>
              {{(hasMappings ? """
              <packageSourceMapping>
                <packageSource key="old-hive"><package pattern="Aspire*" /></packageSource>
                <packageSource key="private"><package pattern="Contoso.*" /></packageSource>
              </packageSourceMapping>
              """ : "")}}
            </configuration>
            """;
        await File.WriteAllTextAsync(configPath, original);
        var service = CreateService();
        var configuration = service.BuildAppHostConfiguration(
            appHostDirectory, "test", [new PackageMapping("Aspire*", newHive)],
            overrideIntegrationSources: false,
            cancellationToken: TestContext.Current.CancellationToken);
        using var preview = await service.CreateConfigurationPreviewAsync(
            appHostDirectory, configuration, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        var previewDirectory = preview.EffectiveWorkingDirectory.FullName;

        Assert.Equal([newHive], NuGetTestHelper.GetEligiblePackageSources(previewDirectory, "Aspire.Hosting"));
        Assert.Equal(hasMappings ? [] : ["https://private.example/v3/index.json"],
            NuGetTestHelper.GetEligiblePackageSources(previewDirectory, "Example.Dependency"));
        Assert.Equal(["https://private.example/v3/index.json"],
            NuGetTestHelper.GetEligiblePackageSources(previewDirectory, "Contoso.Package"));
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));

        var candidate = await new DotNetAppHostNuGetConfigMerger(service).PrepareAsync(
            workspace.WorkspaceRoot, configuration, createIfMissing: false,
            globalPackagesFolder: null, TestContext.Current.CancellationToken);
        Assert.NotNull(candidate);
        await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, TestContext.Current.CancellationToken);
        var persistedLocations = new[] { newHive, "https://private.example/v3/index.json" }.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(hasMappings ? [newHive] : persistedLocations,
            NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Hosting"));
        Assert.Equal(hasMappings ? [] : persistedLocations,
            NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Example.Dependency"));
        var persistedSources = new global::NuGet.Configuration.PackageSourceProvider(
            global::NuGet.Configuration.Settings.LoadDefaultSettings(appHostDirectory.FullName)).LoadPackageSources();
        Assert.Equal(
            new[] { "https://private.example/v3/index.json", newHive }.Order(StringComparer.Ordinal),
            persistedSources.Select(static source => source.Source).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ChannelPolicy_CanChangeGeneratedSourceWithoutReusingItsCredentials()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.WorkspaceRoot;
        await File.WriteAllTextAsync(
            Path.Combine(directory.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="aspire-test" value="https://old.example/v3/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <aspire-test>
                  <add key="Username" value="test-user" />
                  <add key="ClearTextPassword" value="test-password" />
                </aspire-test>
              </packageSourceCredentials>
              <packageSourceMapping>
                <packageSource key="aspire-test"><package pattern="Aspire*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var service = CreateService();
        var configuration = service.BuildAppHostConfiguration(
            directory, "test", [new PackageMapping("Aspire*", "https://new.example/v3/index.json")],
            overrideIntegrationSources: false,
            cancellationToken: TestContext.Current.CancellationToken);
        using var preview = await service.CreateConfigurationPreviewAsync(
            directory, configuration, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        var previewSettings = global::NuGet.Configuration.Settings.LoadDefaultSettings(preview.EffectiveWorkingDirectory.FullName);
        var selectedSource = new global::NuGet.Configuration.PackageSourceProvider(previewSettings).LoadPackageSources()
            .Single(static source => source.IsEnabled);

        Assert.Equal("aspire-test-0", selectedSource.Name);
        Assert.Null(selectedSource.Credentials);
        Assert.Equal(["https://new.example/v3/index.json"], NuGetTestHelper.GetEligiblePackageSources(preview.EffectiveWorkingDirectory.FullName, "Aspire.Hosting"));
        await new DotNetAppHostNuGetConfigMerger(service).CreateOrUpdateAsync(
            directory, configuration, createIfMissing: false, globalPackagesFolder: null,
            confirmationCallback: null, TestContext.Current.CancellationToken);
        Assert.Equal(["https://new.example/v3/index.json"], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, "Aspire.Hosting"));
    }

    [Fact]
    public void IsPackageSourceMappingEnabled_UsesNuGetConfigHierarchy()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectDirectory = workspace.CreateDirectory("AppHost");
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        File.WriteAllText(
            configPath,
            """
            <configuration>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var service = CreateService();

        var enabled = service.IsPackageSourceMappingEnabled(
            projectDirectory,
            TestContext.Current.CancellationToken);

        Assert.True(enabled);
    }

    [Fact]
    public void IsPackageSourceMappingEnabled_MatchesNuGetSectionAndItemSemantics()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var nonCanonicalDirectory = workspace.CreateDirectory("noncanonical");
        var mappingThenClearDirectory = workspace.CreateDirectory("mapping-then-clear");
        var clearThenMappingDirectory = workspace.CreateDirectory("clear-then-mapping");

        File.WriteAllText(
            Path.Combine(nonCanonicalDirectory.FullName, "NuGet.Config"),
            """
            <configuration>
              <PackageSourceMapping>
                <packageSource key="private">
                  <package pattern="*" />
                </packageSource>
              </PackageSourceMapping>
              <packageSourceMapping>
                <clear />
              </packageSourceMapping>
            </configuration>
            """);
        File.WriteAllText(
            Path.Combine(mappingThenClearDirectory.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="*" />
                </packageSource>
                <clear />
              </packageSourceMapping>
            </configuration>
            """);
        File.WriteAllText(
            Path.Combine(clearThenMappingDirectory.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSourceMapping>
                <clear />
                <PackageSource key="private">
                  <package pattern="*" />
                </PackageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var service = CreateService();

        Assert.False(service.IsPackageSourceMappingEnabled(
            nonCanonicalDirectory,
            TestContext.Current.CancellationToken));
        Assert.False(service.IsPackageSourceMappingEnabled(
            mappingThenClearDirectory,
            TestContext.Current.CancellationToken));
        Assert.True(service.IsPackageSourceMappingEnabled(
            clearThenMappingDirectory,
            TestContext.Current.CancellationToken));
    }

    private static BundleNuGetService CreateService()
    {
        var nuGetClient = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);
        var bundleNuGetService = new BundleNuGetService(
            NullLogger<BundleNuGetService>.Instance,
            nuGetClient,
            new TestConfigurationService());
        return bundleNuGetService;
    }
}
