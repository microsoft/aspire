// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class PrebuiltAppHostServerTests(ITestOutputHelper outputHelper)
{
    private const string NuGetOrgSource = "https://api.nuget.org/v3/index.json";

    [Fact]
    public void GenerateIntegrationProjectFile_WithPackagesOnly_ProducesPackageReferences()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")
        };
        var projectRefs = new List<IntegrationReference>();

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var packageElements = doc.Descendants("PackageReference").ToList();
        Assert.Equal(2, packageElements.Count);
        Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "Aspire.Hosting" && e.Attribute("Version")?.Value == "13.2.0");
        Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "13.2.0");

        Assert.Empty(doc.Descendants("ProjectReference"));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithProjectRefsOnly_ProducesProjectReferences()
    {
        var packageRefs = new List<IntegrationReference>();
        var projectRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var projectElements = doc.Descendants("ProjectReference").ToList();
        Assert.Single(projectElements);
        Assert.Equal("/path/to/MyIntegration.csproj", projectElements[0].Attribute("Include")?.Value);

        Assert.Empty(doc.Descendants("PackageReference"));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithMixed_ProducesBothReferenceTypes()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")
        };
        var projectRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/tmp/libs");
        var doc = XDocument.Parse(xml);

        Assert.Equal(2, doc.Descendants("PackageReference").Count());
        Assert.Single(doc.Descendants("ProjectReference"));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_DoesNotSetOutDir()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0")
        };
        var projectRefs = new List<IntegrationReference>();

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/custom/output/path");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Null(doc.Descendants(ns + "OutDir").FirstOrDefault());
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WritesClosureManifestFiles()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/work");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal(Path.Combine("/tmp/work", PrebuiltAppHostServer.ClosureMetadataFileName), doc.Descendants(ns + "AspireClosureMetadataFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", PrebuiltAppHostServer.ClosureSourcesFileName), doc.Descendants(ns + "AspireClosureSourcesFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", PrebuiltAppHostServer.ClosureTargetsFileName), doc.Descendants(ns + "AspireClosureTargetsFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", PrebuiltAppHostServer.ProjectRefAssemblyNamesFileName), doc.Descendants(ns + "AspireProjectRefAssemblyNamesFile").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WritesClosureManifestTarget()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/work");
        var doc = XDocument.Parse(xml);

        var target = doc.Descendants("Target")
            .FirstOrDefault(element => element.Attribute("Name")?.Value == "_WriteAspireClosureManifest");

        Assert.NotNull(target);
        Assert.Equal("Build", target.Attribute("AfterTargets")?.Value);
        Assert.Equal("ResolveLockFileCopyLocalFiles", target.Attribute("DependsOnTargets")?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_HasCopyLocalLockFileAssemblies()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var copyLocal = doc.Descendants(ns + "CopyLocalLockFileAssemblies").FirstOrDefault()?.Value;
        Assert.Equal("true", copyLocal);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_DisablesAnalyzersAndDocGen()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal("false", doc.Descendants(ns + "EnableNETAnalyzers").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "GenerateDocumentationFile").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "ProduceReferenceAssembly").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_TargetsNet10()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal("net10.0", doc.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithAdditionalSources_SetsRestoreAdditionalProjectSources()
    {
        var sources = new[] { "/local/packages", "https://my-feed/v3/index.json" };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs", sources);
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var restoreSources = doc.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault()?.Value;
        Assert.NotNull(restoreSources);
        Assert.Contains("/local/packages", restoreSources);
        Assert.Contains("https://my-feed/v3/index.json", restoreSources);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithRestoreConfigFile_SetsRestoreConfigFile()
    {
        var sources = new[] { "/local/packages", "https://my-feed/v3/index.json" };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(
            [],
            [],
            "/tmp/libs",
            sources,
            restoreConfigFile: "/tmp/nuget.config");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var restoreConfigFile = doc.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
        var restoreSources = doc.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault();
        Assert.Equal("/tmp/nuget.config", restoreConfigFile);
        Assert.Null(restoreSources);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithEmptyAdditionalSources_DoesNotSetRestoreAdditionalProjectSources()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs", Enumerable.Empty<string>());
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var restoreSources = doc.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault();
        Assert.Null(restoreSources);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithExactVersions_ExactPinsOnlyAspirePackages()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17166.ga49d604d"),
            IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
        };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(
            packageRefs,
            [],
            "/tmp/libs",
            useExactPackageVersions: true);
        var doc = XDocument.Parse(xml);

        var packageElements = doc.Descendants("PackageReference").ToList();
        Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "[13.4.0-pr.17166.ga49d604d]");
        Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "CommunityToolkit.Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "1.0.0");
    }

    [Fact]
    public void Constructor_UsesWorkspaceAspireDirectoryForWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var nugetService = new BundleNuGetService(new NullLayoutDiscovery(), new LayoutProcessRunner(new TestProcessExecutionFactory()), new TestFeatures(), TestExecutionContextFactory.CreateTestContext(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);
        var server = new PrebuiltAppHostServer(
            appHostDirectory.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            Aspire.Cli.Tests.Mcp.MockPackagingServiceFactory.Create(),
            Aspire.Cli.Tests.Mcp.TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var workingDirectory = Assert.IsType<string>(
            typeof(PrebuiltAppHostServer)
                .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(server));

        var rootDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "apphosts");
        var isUnderRoot = workingDirectory.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase);
        var parentDirectory = Path.GetDirectoryName(workingDirectory);
        var isDirectChildOfRoot = parentDirectory is not null &&
                                   string.Equals(parentDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase);
        var isSafeToDelete = isUnderRoot && isDirectChildOfRoot && !string.Equals(workingDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase);

        try
        {
            Assert.True(isSafeToDelete);
        }
        finally
        {
            if (isSafeToDelete && Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Constructor_UsesDistinctWorkingDirectoriesForMultipleAppHostsInSameWorkspace()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var firstAppHost = workspace.CreateDirectory(Path.Combine("apps", "api"));
        var secondAppHost = workspace.CreateDirectory(Path.Combine("apps", "web"));

        var nugetService = new BundleNuGetService(
            new NullLayoutDiscovery(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);

        PrebuiltAppHostServer CreateServer(string appHostDirectory) => new(
            appHostDirectory,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            Aspire.Cli.Tests.Mcp.MockPackagingServiceFactory.Create(),
            Aspire.Cli.Tests.Mcp.TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var firstServer = CreateServer(firstAppHost.FullName);
        var secondServer = CreateServer(secondAppHost.FullName);

        var workingDirectoryField = typeof(PrebuiltAppHostServer)
            .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var firstWorkingDirectory = Assert.IsType<string>(workingDirectoryField.GetValue(firstServer));
        var secondWorkingDirectory = Assert.IsType<string>(workingDirectoryField.GetValue(secondServer));

        var appHostsRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "apphosts");

        try
        {
            Assert.StartsWith(appHostsRoot, firstWorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(appHostsRoot, secondWorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(firstWorkingDirectory, secondWorkingDirectory);
        }
        finally
        {
            foreach (var dir in new[] { firstWorkingDirectory, secondWorkingDirectory })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    // PSM-guard cross-product tests.
    // Guard predicate: the resolved channel.Name == "local" — i.e. the *project requested* the
    // local pseudo-channel. The local hive has no real mappings, so emitting PSM would just
    // constrain restore to nothing. For every other channel PSM must emit so restore honours the
    // channel's package source mappings — regardless of which CLI identity is running.

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_LocalIdentity_LocalRequested_ReturnsNull()
    {
        // Locally-built CLI consuming its own local hive — only case the guard should fire.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_LocalIdentity_PrRequested_EmitsConfig()
    {
        // Locally-built CLI on a project that requested pr-12345 — the project's request wins,
        // PSM must emit (this is the scenario that regressed pre-fix).
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        var server = CreateServerWithExplicitChannel(workspace, "pr-12345", executionContext);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "pr-12345");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_StableIdentity_StableRequested_EmitsConfig()
    {
        // Stable-channel CLI on a project that requested 'stable' — PSM emits the stable mappings.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("stable");
        var server = CreateServerWithExplicitChannel(workspace, "stable", executionContext);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "stable");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_StableIdentity_LocalRequested_ReturnsNull()
    {
        // requested=local always returns null regardless of identity: the guard keys on the
        // requested/resolved channel name, not on which CLI is running.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("stable");
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_DailyIdentity_DailyRequested_EmitsConfig()
    {
        // A 'daily' CLI consuming the 'daily' channel must still get a per-channel NuGet config —
        // the guard only fires when the *requested* channel is 'local'.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        var server = CreateServerWithExplicitChannel(workspace, "daily", executionContext);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "daily");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_PrIdentity_DifferentPrRequested_EmitsConfig()
    {
        // PR-build CLI installing a different PR's hive — guard does not fire (requested != "local").
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("pr-67890");
        var server = CreateServerWithExplicitChannel(workspace, "pr-12345", executionContext);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "pr-12345");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_LocalIdentity_StagingRequested_EmitsConfigWithGlobalPackagesFolder()
    {
        // Pins the rubber-duck finding: dropping the temp config also drops the staging-specific
        // global packages folder. The emitted nuget.config must contain a <config> element with a
        // globalPackagesFolder setting when the channel was built with configureGlobalPackagesFolder.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: mappings,
            nuGetPackageCache: new FakeNuGetPackageCache(),
            configureGlobalPackagesFolder: true);
        var server = CreateServerWithChannel(workspace, stagingChannel, executionContext);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "staging");

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        var gpf = doc.Descendants("config")
            .SelectMany(c => c.Elements("add"))
            .FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gpf);
        Assert.False(string.IsNullOrEmpty(gpf.Attribute("value")?.Value));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("pr-99")]
    public async Task TryCreateTemporaryNuGetConfig_LocalRequested_ReturnsNull_RegardlessOfIdentity(string identity)
    {
        // Codifies "the local hive resolution skip is identity-independent". PackagingService
        // enumerates HivesDirectory subdirs as explicit channels, so a project requesting "local"
        // resolves to an explicit channel with mappings — but the new guard fires because
        // channel.Name == "local".
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel(identity);
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_WithPackageSourceOverride_MapsAspireToOverrideAndAddsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([])
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(
            server,
            requestedChannel: null,
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_WithPackageSourceOverrideWithoutRequestedChannel_DoesNotMergeExplicitChannelAspireMappings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var explicitChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var server = CreateServerWithChannel(workspace, explicitChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(
            server,
            requestedChannel: null,
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Empty(GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_WithPackageSourceOverride_PreservesRequestedChannelMappingsAndGlobalPackagesFolder()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("CommunityToolkit*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            configureGlobalPackagesFolder: true);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal(["CommunityToolkit*"], GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
        Assert.NotNull(doc.Descendants("config")
            .SelectMany(c => c.Elements("add"))
            .FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_WithPackageSourceOverride_DropsRequestedChannelAspireMappings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource), new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Empty(GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_WithPackageSourceOverride_PassesRequestedChannelToPackagingService()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([])
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(
            server,
            requestedChannel: PackageChannelNames.Staging,
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        Assert.Equal(PackageChannelNames.Staging, packagingService.LastRequestedChannelName);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_WithPackageSourceOverride_UsesChannelAllPackagesMappingAsFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping(PackageMapping.AllPackages, channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, channelSource));
        Assert.Empty(GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_WithPackageSourceOverride_WhenChannelLookupFails_StillCreatesOverrideConfigWithFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channel lookup failed.")
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task GetNuGetSources_WithPackageSourceOverrideAndMatchedChannel_OmitsChannelAspireFeedFromSources()
    {
        // Regression: the temp NuGet.config drops the matched channel's Aspire* mapping in
        // the override branch, but the --source argument list passed to the bundled NuGet
        // tool also has to drop that source URL. The bundled tool treats extra `--source`
        // CLI args as co-eligible with config mappings, so re-adding the channel's Aspire
        // feed here would silently let Aspire packages resolve from it and defeat the
        // override.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await InvokeGetNuGetSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.DoesNotContain(channelSource, sources);
        Assert.Contains(NuGetOrgSource, sources);
    }

    [Fact]
    public async Task GetNuGetSources_WithPackageSourceOverrideAndMatchedChannelNonAspireMapping_KeepsChannelSourceAndAddsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("CommunityToolkit*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await InvokeGetNuGetSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.Contains(channelSource, sources);
        // Matched channel has no AllPackages mapping, so the temp NuGet.config uses NuGet.org
        // as catch-all and the sources list must include it too.
        Assert.Contains(NuGetOrgSource, sources);
    }

    [Fact]
    public async Task GetNuGetSources_WithPackageSourceOverrideAndMatchedChannelAllPackagesMapping_OmitsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping(PackageMapping.AllPackages, channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await InvokeGetNuGetSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.Contains(channelSource, sources);
        // Matched channel supplied its own AllPackages mapping, so NuGet.org should not be
        // added as a co-eligible source — the channel's catch-all wins in both the temp
        // config's PSM and the --source argument list.
        Assert.DoesNotContain(NuGetOrgSource, sources);
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", "https://api.nuget.org/v3/index.json")]
    [InlineData("/tmp/aspire-packages", "/tmp/aspire-packages")]
    [InlineData(@"C:\packages", @"C:\packages")]
    [InlineData("https://user:pat@feed.example.com/v3/index.json", "https://***@feed.example.com/v3/index.json")]
    [InlineData("https://feed.blob.core.windows.net/foo/index.json?sv=2024-01&sig=secret-sig", "https://feed.blob.core.windows.net/foo/index.json")]
    [InlineData("https://user:pat@feed.example.com/v3/index.json?sig=secret", "https://***@feed.example.com/v3/index.json")]
    [InlineData("https://feed.example.com/v3/index.json#fragment", "https://feed.example.com/v3/index.json")]
    public void RedactSourceForDisplay_StripsCredentialsAndQueryFromHttpUrlsButPreservesPlainSources(string input, string expected)
    {
        Assert.Equal(expected, PrebuiltAppHostServer.RedactSourceForDisplay(input));
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_StagingRequested_RefusesWhenPackagingServiceReportsUnavailable()
    {
        // Regression for radical's review of #17235: on a daily/local/pr CLI the packaging service
        // refuses to synthesize a 'staging' channel and surfaces the actionable reason. The bundled
        // AppHost restore must not silently fall through to a different feed — it must propagate
        // that reason so the user sees the same message the update/new commands now show.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeTryCreateTemporaryNuGetConfigAsync(server, "staging"));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_StagingRequestedWithSourceOverride_RefusesWhenPackagingServiceReportsUnavailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeTryCreateTemporaryNuGetConfigAsync(server, "staging", "/tmp/aspire-pr-hive/packages"));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task GetNuGetSources_StagingRequested_RefusesWhenPackagingServiceReportsUnavailable()
    {
        // Companion of the TryCreateTemporaryNuGetConfig test above. Without this guard,
        // GetNuGetSourcesAsync's "no match -> all explicit channels" fallback hands the
        // shared daily feed to nuget restore on a daily-identity CLI even though the project
        // pinned channel: staging.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.GetNuGetSourcesAsync("staging", packageSourceOverride: null, CancellationToken.None));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task GetNuGetSources_NonStagingRequest_NotAffectedByStagingUnavailableReason()
    {
        // Negative control: the staging refusal must only fire for requestedChannel == "staging".
        // A request for any other channel name must continue to resolve normally even when the
        // packaging service is reporting staging-unavailable.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            "daily", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel]),
            GetStagingChannelUnavailableReasonCallback = () => "Staging unavailable"
        };

        var nugetService = new BundleNuGetService(
            new NullLayoutDiscovery(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            executionContext,
            NullLogger<BundleNuGetService>.Instance);

        var server = new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            packagingService,
            executionContext,
            NullLogger.Instance);

        var sources = await server.GetNuGetSourcesAsync("daily", packageSourceOverride: null, CancellationToken.None);

        Assert.NotNull(sources);
        Assert.Contains("https://pkgs.dev.azure.com/fake/v3/index.json", sources);
    }

    private static PrebuiltAppHostServer CreateServerWithUnavailableStagingChannel(
        TemporaryWorkspace workspace,
        CliExecutionContext executionContext,
        string unavailableReason)
    {
        // Mirrors what PackagingService does on a daily/local/pr CLI: omits 'staging' from
        // GetChannelsAsync and surfaces the actionable reason via GetStagingChannelUnavailableReason.
        // We hand back the 'daily' channel because that is the shared explicit channel the
        // pre-fix fallback path would have silently picked up.
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            "daily", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache());

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel]),
            GetStagingChannelUnavailableReasonCallback = () => unavailableReason
        };

        var nugetService = new BundleNuGetService(
            new NullLayoutDiscovery(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            executionContext,
            NullLogger<BundleNuGetService>.Instance);

        return new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            packagingService,
            executionContext,
            NullLogger.Instance);
    }

    private static CliExecutionContext CreateContextWithIdentityChannel(string identityChannel) =>
        new(new DirectoryInfo(Path.GetTempPath()),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hives")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "cache")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "sdks")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "logs")),
            "test.log",
            identityChannel: identityChannel);

    private static PrebuiltAppHostServer CreateServerWithExplicitChannel(
        TemporaryWorkspace workspace,
        string channelName,
        CliExecutionContext executionContext)
    {
        // channelName is the name of the channel registered in the TestPackagingService — i.e. the
        // channel a project's aspire.config.json would resolve to when it requests that name.
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var channel = PackageChannel.CreateExplicitChannel(
            channelName, PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache());
        return CreateServerWithChannel(workspace, channel, executionContext);
    }

    private static PrebuiltAppHostServer CreateServerWithChannel(
        TemporaryWorkspace workspace,
        PackageChannel channel,
        CliExecutionContext executionContext)
    {
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        return CreateServerWithPackagingService(workspace, packagingService, executionContext);
    }

    private static PrebuiltAppHostServer CreateServerWithPackagingService(
        TemporaryWorkspace workspace,
        IPackagingService packagingService,
        CliExecutionContext? executionContext = null)
    {
        executionContext ??= TestExecutionContextFactory.CreateTestContext();
        var nugetService = new BundleNuGetService(
            new NullLayoutDiscovery(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            executionContext,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);

        return new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            packagingService,
            executionContext,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    private static async Task<TemporaryNuGetConfig?> InvokeTryCreateTemporaryNuGetConfigAsync(
        PrebuiltAppHostServer server,
        string? requestedChannel,
        string? packageSourceOverride = null)
    {
        var method = typeof(PrebuiltAppHostServer).GetMethod(
            "TryCreateTemporaryNuGetConfigAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<TemporaryNuGetConfig?>)method.Invoke(server, [requestedChannel, packageSourceOverride, CancellationToken.None])!;
        return await task;
    }

    private static async Task<IReadOnlyList<string>?> InvokeGetNuGetSourcesAsync(
        PrebuiltAppHostServer server,
        string? requestedChannel,
        string? packageSourceOverride = null)
    {
        var method = typeof(PrebuiltAppHostServer).GetMethod(
            "GetNuGetSourcesAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<IEnumerable<string>?>)method.Invoke(server, [requestedChannel, packageSourceOverride, CancellationToken.None])!;
        var result = await task;
        return result?.ToList();
    }

    [Fact]
    public async Task ResolveRequestedChannel_UsesProjectLocalAspireConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-new"
            }
            """);

        var nugetService = new BundleNuGetService(new NullLayoutDiscovery(), new LayoutProcessRunner(new TestProcessExecutionFactory()), new TestFeatures(), TestExecutionContextFactory.CreateTestContext(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);
        var server = new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            Aspire.Cli.Tests.Mcp.MockPackagingServiceFactory.Create(),
            Aspire.Cli.Tests.Mcp.TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var channel = server.ResolveRequestedChannel();

        Assert.Equal("pr-new", channel);
    }

    [Fact]
    public async Task PrepareAsync_WithNoIntegrations_WritesDefaultAppSettings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var nugetService = new BundleNuGetService(new NullLayoutDiscovery(), new LayoutProcessRunner(new TestProcessExecutionFactory()), new TestFeatures(), TestExecutionContextFactory.CreateTestContext(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);
        var server = new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            MockPackagingServiceFactory.Create(),
            TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", []);

            Assert.True(result.Success);
            Assert.Null(server.SelectedProjectLayoutPath);

            var appSettingsPath = Path.Combine(workingDirectory, "appsettings.json");
            Assert.True(File.Exists(appSettingsPath));

            var appSettingsContent = await File.ReadAllTextAsync(appSettingsPath);
            Assert.Contains("\"Aspire.Hosting\"", appSettingsContent);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferences_SetsOnlyPackageProbeManifest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")]);

            Assert.True(result.Success);
            Assert.Null(server.SelectedProjectLayoutPath);
            Assert.Equal(2, executionFactory.AttemptCount);

            var manifestPath = Assert.IsType<string>(server.IntegrationProbeManifestPath);
            Assert.StartsWith(
                Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore"),
                manifestPath,
                StringComparison.OrdinalIgnoreCase);

            var startInfo = server.CreateStartInfo(123);
            Assert.Equal(manifestPath, startInfo.Environment[KnownConfigNames.IntegrationProbeManifestPath]);
            Assert.False(startInfo.Environment.ContainsKey(KnownConfigNames.IntegrationLibsPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferences_UsesPackageSourceOverride()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        List<string>? restoreArgs = null;

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([packageSourceOverride, NuGetOrgSource], GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
            Assert.Contains("CommunityToolkit.Aspire.Hosting.Redis,1.0.0", restoreArgs!);
            Assert.Contains("--nuget-config", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageSourceOverride_AddsNuGetOrgFallbackSource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        List<string>? restoreArgs = null;

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([packageSourceOverride, NuGetOrgSource], GetSourceArguments(restoreArgs!));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Theory]
    [InlineData("pr-12345")]
    [InlineData("local")]
    [InlineData("worktree-feature")]
    public async Task PrepareAsync_WithHiveBackedChannel_UsesLocalAspireSourceAsOverride(string channelName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packageSource = workspace.CreateDirectory("hive-packages");
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, $$"""
            {
                "channel": "{{channelName}}"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: channelName,
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", packageSource.FullName)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([packageSource.FullName, NuGetOrgSource], GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
            Assert.Contains("CommunityToolkit.Aspire.Hosting.Redis,1.0.0", restoreArgs!);
            Assert.Contains("--nuget-config", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithExplicitPackageSourceOverride_IgnoresHiveBackedAspireSource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var explicitPackageSource = workspace.CreateDirectory("explicit-packages");
        var hivePackageSource = workspace.CreateDirectory("hive-packages");
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", hivePackageSource.FullName),
                new PackageMapping(PackageMapping.AllPackages, channelSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: explicitPackageSource.FullName);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([explicitPackageSource.FullName, channelSource], GetSourceArguments(restoreArgs!));
            Assert.DoesNotContain(hivePackageSource.FullName, restoreArgs!);
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithHttpBackedChannel_DoesNotUseExactPackageVersions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([channelSource], GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,13.4.0-pr.17141.gf142085f", restoreArgs!);
            Assert.DoesNotContain("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenPackagingServiceThrowsDuringAutoDiscovery_DegradesGracefully()
    {
        // Regression guard: an unexpected failure from IPackagingService.GetChannelsAsync during
        // hive-source auto-discovery must NOT turn `aspire new` into a hard failure. Both call
        // sites that resolve channels for an effective package source — ResolveLocalPackageSource-
        // OverrideAsync and TryCreateTemporaryNuGetConfigAsync's no-override branch — catch
        // transient exceptions and fall through to "no override discovered" / "no PSM-bearing
        // temp config", matching the defensive catch in GetNuGetSourcesAsync.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string channelName = "pr-12345";
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, $$"""
            {
                "channel": "{{channelName}}"
            }
            """);

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromException<IEnumerable<PackageChannel>>(
                new InvalidOperationException("simulated packaging service failure"))
        };
        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            // No override resolved → no exact version pinning, no synthesized [override, nuget.org] source set.
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,13.4.0-pr.17141.gf142085f", restoreArgs!);
            Assert.DoesNotContain("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithHiveBackedChannelPointingAtMissingLocalDirectory_DoesNotApplyOverride()
    {
        // Negative case for ResolveLocalPackageSourceOverrideAsync: a stale aspire.config.json
        // (e.g. user pinned channel = "pr-12345" but later deleted the local hive directory)
        // must NOT cause the prebuilt restore to pin Aspire packages to a non-existent local
        // directory. GetExistingLocalAspirePackageSource skips mappings whose Source does not
        // exist on disk, so auto-discovery returns null and restore falls through to the
        // ambient + channel-source path with no exact-pin.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var missingPackageSource = Path.Combine(workspace.WorkspaceRoot.FullName, "this-hive-was-deleted");
        Assert.False(Directory.Exists(missingPackageSource));
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", missingPackageSource)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            // The override was not applied (Directory.Exists check failed), so the source list
            // is just the channel's raw Aspire mapping with no NuGet.org fallback appended (the
            // fallback only fires on the override path), and no exact-pin is emitted. Contrast
            // with PrepareAsync_WithHiveBackedChannel_UsesLocalAspireSourceAsOverride where the
            // existing local directory promotes the channel source to an override and adds the
            // NuGet.org fallback + exact-pinning.
            Assert.Equal([missingPackageSource], GetSourceArguments(restoreArgs!));
            Assert.DoesNotContain(NuGetOrgSource, GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,13.4.0-pr.17141.gf142085f", restoreArgs!);
            Assert.DoesNotContain("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithSourceAndChannelHavingAspireMapping_TempConfigDropsChannelAspireMapping()
    {
        // End-to-end check that `aspire new --source <pr> --channel <X>` does not let the channel's
        // Aspire* feed remain co-eligible with the override at restore time. The unit-level
        // TryCreateTemporaryNuGetConfig_* cases pin the generator; this case pins that PrepareAsync
        // wires that same temp config through to the actual restore invocation.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        XDocument? tempConfigDoc = null;
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                // Read the temp NuGet.config while it still exists; it is disposed when
                // PrepareAsync's inner `using var` exits, which races with our assertions.
                var argsList = (IReadOnlyList<string>)args;
                var nugetConfigIndex = -1;
                for (var i = 0; i < argsList.Count - 1; i++)
                {
                    if (argsList[i] == "--nuget-config")
                    {
                        nugetConfigIndex = i;
                        break;
                    }
                }

                if (nugetConfigIndex >= 0)
                {
                    tempConfigDoc = XDocument.Load(argsList[nugetConfigIndex + 1]);
                }
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.Equal("daily", result.ChannelName);
            Assert.NotNull(tempConfigDoc);

            // The temp config is the authoritative PSM gate. Verify the channel's Aspire* mapping
            // was dropped — only the override serves Aspire packages.
            Assert.Equal(["Aspire*"], GetPackagePatternsForSource(tempConfigDoc!, packageSourceOverride));
            Assert.Empty(GetPackagePatternsForSource(tempConfigDoc!, channelSource));
            Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(tempConfigDoc!, NuGetOrgSource));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_OutputIncludesSourceAndChannelContext()
    {
        // When restore fails, the displayed output is the only debugging surface most users see.
        // Pin that --source and the requested channel are present so a failed
        // `aspire new --source <X> --channel <Y>` doesn't require re-running with diagnostic logs
        // just to recover which inputs were in play.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        // Fail the restore step itself; BundleNuGetService throws on non-zero exit which
        // propagates through PrepareAsync's outer catch.
        executionFactory.DefaultExitCode = 1;

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: packageSourceOverride);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            Assert.Contains($"--source: {packageSourceOverride}", combined);
            Assert.Contains("channel:  daily", combined);
            Assert.Contains("packages: Aspire.Hosting.CodeGeneration.TypeScript 13.4.0-pr.17141.gf142085f", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_WithManyPackages_TruncatesPackageList()
    {
        // The package preview caps at 5 entries with a "(+N more)" suffix so the error footer
        // doesn't explode for projects with large package counts. Pin the truncation shape so
        // it can't silently regress.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.DefaultExitCode = 1;

        var packages = Enumerable.Range(0, 8)
            .Select(i => IntegrationReference.FromPackage($"Aspire.Hosting.Pkg{i}", "1.0.0"))
            .ToArray();

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                packages,
                packageSourceOverride: packageSourceOverride);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            // First five packages appear; later ones are collapsed into a count.
            Assert.Contains("Aspire.Hosting.Pkg0 1.0.0", combined);
            Assert.Contains("Aspire.Hosting.Pkg4 1.0.0", combined);
            Assert.DoesNotContain("Aspire.Hosting.Pkg5 1.0.0", combined);
            Assert.DoesNotContain("Aspire.Hosting.Pkg7 1.0.0", combined);
            Assert.Contains("(+3 more)", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferencesAndExplicitChannelButNoOverride_UsesAdditionalSourcesNotRestoreConfigFile()
    {
        // Regression for finding #1 of the 2026-05-19 post-merge review: a project-ref restore
        // with an explicit channel pin (daily/staging/pr-*) and NO --source must not replace the
        // user's ambient nuget.config via <RestoreConfigFile>. The channel sources flow through
        // additively via <RestoreAdditionalProjectSources> so private/internal feeds the user
        // has configured in nuget.config remain reachable for non-Aspire transitives.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        XDocument? generatedProject = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, _, _) =>
            {
                generatedProject = XDocument.Load(projectFilePath.FullName);
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };

        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };

        var nugetService = new BundleNuGetService(
            new NullLayoutDiscovery(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);
        var server = new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            dotNetCliRunner,
            new TestDotNetSdkInstaller(),
            packagingService,
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger.Instance);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(generatedProject);

            var ns = generatedProject!.Root!.GetDefaultNamespace();
            Assert.Null(generatedProject.Descendants(ns + "RestoreConfigFile").FirstOrDefault());

            var restoreSources = generatedProject.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault()?.Value;
            Assert.NotNull(restoreSources);
            Assert.Contains(channelSource, restoreSources!);

            // Aspire package versions remain in their original (non-pinned) form when no override
            // is in play; the exact-version pinning only fires when a single source is selected.
            var packageElements = generatedProject.Descendants("PackageReference").ToList();
            Assert.Contains(packageElements, e =>
                e.Attribute("Include")?.Value == "Aspire.Hosting.Redis" &&
                e.Attribute("Version")?.Value == "13.4.0-pr.17141.gf142085f");
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_WithAutoDiscoveredLocalSource_FooterShowsEffectiveSource()
    {
        // Regression for finding #3 of the 2026-05-19 post-merge review: when the caller passes
        // no --source but ResolveLocalPackageSourceOverrideAsync auto-discovers a local hive,
        // the failure footer must reflect the source actually used by restore. Previously the
        // catch blocks read the original (unset) `packageSourceOverride` argument and the user
        // saw only the channel name, hiding that a local hive participated in the failed
        // restore.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var localHive = workspace.CreateDirectory("local-aspire-hive").FullName;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var prChannel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", localHive)],
            nuGetPackageCache: new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([prChannel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.DefaultExitCode = 1;

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.12345.gabcdef00",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.12345.gabcdef00")]);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            Assert.Contains($"--source: {localHive}", combined);
            Assert.Contains("channel:  pr-12345", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Theory]
    [InlineData("https://user:p@ss@host/path", "<unparseable http source>")]
    [InlineData("https://user:p#word@host/", "<unparseable http source>")]
    [InlineData("http://foo bar/path", "<unparseable http source>")]
    [InlineData("HTTPS://user:p@ss@host/path", "<unparseable http source>")]
    [InlineData("/tmp/aspire/some path with [brackets]", "/tmp/aspire/some path with [brackets]")]
    public void RedactSourceForDisplay_FailsClosedForMalformedHttpButPassesThroughLocalPaths(string input, string expected)
    {
        // Regression for finding #5 of the 2026-05-19 post-merge review: HTTP-looking inputs that
        // Uri.TryCreate cannot parse (e.g. unescaped @ or # in user-info, embedded whitespace)
        // must return the sentinel rather than the raw input, otherwise credentials embedded in
        // the malformed URL would leak through the failure footer / bug reports. Plain non-HTTP
        // inputs continue to pass through unchanged because they don't carry credentials.
        Assert.Equal(expected, PrebuiltAppHostServer.RedactSourceForDisplay(input));
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferencesAndPackageSourceOverride_UsesNuGetConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        XDocument? generatedProject = null;
        bool restoreConfigFileExistedDuringBuild = false;

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, _, _) =>
            {
                generatedProject = XDocument.Load(projectFilePath.FullName);
                var ns = generatedProject.Root!.GetDefaultNamespace();
                var restoreConfigFile = generatedProject.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
                restoreConfigFileExistedDuringBuild = restoreConfigFile is not null && File.Exists(restoreConfigFile);
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };
        var nugetService = new BundleNuGetService(
            new NullLayoutDiscovery(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);
        var server = new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            dotNetCliRunner,
            new TestDotNetSdkInstaller(),
            MockPackagingServiceFactory.Create(),
            TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17166.ga49d604d",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17166.ga49d604d"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(generatedProject);

            var ns = generatedProject.Root!.GetDefaultNamespace();
            var restoreConfigFile = generatedProject.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
            Assert.NotNull(restoreConfigFile);
            Assert.True(restoreConfigFileExistedDuringBuild);
            Assert.Null(generatedProject.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault());

            var packageElements = generatedProject.Descendants("PackageReference").ToList();
            Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "[13.4.0-pr.17166.ga49d604d]");
            Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "CommunityToolkit.Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "1.0.0");
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithStagingPinnedProjectOutsideLaunchDirectory_UsesStagingSourcesAndNuGetConfig()
    {
        const string stagingFeed = "https://example.com/staging/v3/index.json";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDirectory = workspace.CreateDirectory("elsewhere");
        var config = AspireConfigFile.LoadOrCreate(projectDirectory.FullName);
        config.Channel = PackageChannelNames.Staging;
        config.Save(projectDirectory.FullName);

        var layout = CreateBundleLayout(workspace);
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            workspace.WorkspaceRoot,
            identityChannel: PackageChannelNames.Stable);

        string[]? restoreInvocation = null;
        string? temporaryNuGetConfigContent = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) =>
            {
                if (args.Length > 1 &&
                    args[0] == "nuget" &&
                    args[1] == "restore")
                {
                    restoreInvocation = args.ToArray();
                    temporaryNuGetConfigContent = File.ReadAllText(GetArgumentValue(args, "--nuget-config"));
                }
            }
        };

        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            executionContext,
            NullLogger<BundleNuGetService>.Instance);

        var stagingChannel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Staging,
            PackageChannelQuality.Both,
            [
                new PackageMapping("Aspire*", stagingFeed),
                new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
            ],
            new FakeNuGetPackageCache());
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([stagingChannel])
        };

        var server = new PrebuiltAppHostServer(
            projectDirectory.FullName,
            "test.sock",
            layout,
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            packagingService,
            executionContext,
            NullLogger.Instance);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")]);

            Assert.True(result.Success);
            Assert.Equal(PackageChannelNames.Staging, result.ChannelName);

            Assert.NotNull(restoreInvocation);
            Assert.Contains(stagingFeed, restoreInvocation!);
            Assert.Contains(projectDirectory.FullName, restoreInvocation!);
            Assert.NotNull(temporaryNuGetConfigContent);
            Assert.Contains(stagingFeed, temporaryNuGetConfigContent!);
            Assert.Contains("Aspire*", temporaryNuGetConfigContent!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithOnlyProjectReferences_SetsOnlyProjectLayout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };

        var layout = CreateBundleLayout(workspace);
        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], layout: layout);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")]);

            Assert.True(result.Success);
            Assert.Null(server.IntegrationProbeManifestPath);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            Assert.True(File.Exists(Path.Combine(layoutPath, "libs", "MyIntegration.dll")));

            var startInfo = server.CreateStartInfo(123);
            Assert.Equal(Path.Combine(layoutPath, "libs"), startInfo.Environment[KnownConfigNames.IntegrationLibsPath]);
            Assert.False(startInfo.Environment.ContainsKey(KnownConfigNames.IntegrationProbeManifestPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_ReusesProjectLayoutWhenClosureIsUnchanged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);
            Assert.Equal(firstLayoutPath, server.SelectedProjectLayoutPath);
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_WritesPackageProbeManifestAndCopiesOnlyProjectOutputs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(result.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedLibs = Directory.GetFiles(Path.Combine(layoutPath, "libs"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(layoutPath, "libs"), path).Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["MyIntegration.dll"], copiedLibs);

            var probeManifestPath = Assert.IsType<string>(server.IntegrationProbeManifestPath);
            await using var probeManifestStream = File.OpenRead(probeManifestPath);
            using var probeManifest = await JsonDocument.ParseAsync(probeManifestStream);

            var managedAssemblies = probeManifest.RootElement.GetProperty("managedAssemblies").EnumerateArray().ToList();
            Assert.Contains(
                managedAssemblies,
                assembly => assembly.GetProperty("name").GetString() == "Aspire.Hosting.Redis" &&
                    assembly.GetProperty("path").GetString() == Path.Combine(workingDirectory, "integration-restore", "closure-sources", "Aspire.Hosting.Redis.dll"));
            Assert.Equal(0, probeManifest.RootElement.GetProperty("nativeLibraries").GetArrayLength());
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_WritesPackageResourcesAndNativeAssetsToProbeManifest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["fr/Aspire.Hosting.Redis.resources.dll"] = "redis-fr",
            ["runtimes/test-rid/native/testnative.so"] = "native",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = new Dictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/Aspire.Hosting.Redis.dll", "runtime"),
            ["fr/Aspire.Hosting.Redis.resources.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/fr/Aspire.Hosting.Redis.resources.dll", "resources"),
            ["runtimes/test-rid/native/testnative.so"] = ("Aspire.Hosting.Redis", "13.2.0", "runtimes/test-rid/native/testnative.so", "native")
        };

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(result.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedLibs = Directory.GetFiles(Path.Combine(layoutPath, "libs"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(layoutPath, "libs"), path).Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["MyIntegration.dll"], copiedLibs);

            var probeManifestPath = Assert.IsType<string>(server.IntegrationProbeManifestPath);
            await using var probeManifestStream = File.OpenRead(probeManifestPath);
            using var probeManifest = await JsonDocument.ParseAsync(probeManifestStream);

            var managedAssemblies = probeManifest.RootElement.GetProperty("managedAssemblies").EnumerateArray().ToList();
            Assert.Contains(
                managedAssemblies,
                assembly => assembly.GetProperty("name").GetString() == "Aspire.Hosting.Redis" &&
                    !assembly.TryGetProperty("culture", out _));
            Assert.Contains(
                managedAssemblies,
                assembly => assembly.GetProperty("name").GetString() == "Aspire.Hosting.Redis.resources" &&
                    assembly.GetProperty("culture").GetString() == "fr");

            var nativeLibraries = probeManifest.RootElement.GetProperty("nativeLibraries").EnumerateArray().ToList();
            Assert.Contains(
                nativeLibraries,
                nativeLibrary => nativeLibrary.GetProperty("fileName").GetString() == "testnative.so");
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_CreatesNewProjectLayoutWhenClosureChanges()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);

            closureFiles["MyIntegration.dll"] = "integration-v2";

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);

            var secondLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            Assert.NotEqual(firstLayoutPath, secondLayoutPath);
            Assert.True(Directory.Exists(firstLayoutPath));
            Assert.True(Directory.Exists(secondLayoutPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_RecreatesProjectLayoutWhenCachedLayoutIsCorrupt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedFilePath = Path.Combine(layoutPath, "libs", "MyIntegration.dll");
            await File.WriteAllTextAsync(copiedFilePath, "corrupt");

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);

            Assert.Equal(layoutPath, server.SelectedProjectLayoutPath);
            Assert.Equal("integration-v1", await File.ReadAllTextAsync(copiedFilePath));
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_DoesNotTouchLockedPreviousProjectLayout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var lockedFilePath = Path.Combine(firstLayoutPath, "libs", "MyIntegration.dll");

            using (var lockedFile = new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                closureFiles["MyIntegration.dll"] = "integration-v2";

                var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
                Assert.True(secondResult.Success);

                var secondLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
                Assert.NotEqual(firstLayoutPath, secondLayoutPath);
                Assert.True(File.Exists(lockedFilePath));
                Assert.True(File.Exists(Path.Combine(secondLayoutPath, "libs", "MyIntegration.dll")));
            }
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public void ClosureManifest_WithPackageBackedEntries_ChangesFingerprintWhenPackageSourcePathChanges()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var firstPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-a");
        var secondPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-b");
        var firstSourcePath = Path.Combine(firstPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var secondSourcePath = Path.Combine(secondPackageRoot.FullName, "Aspire.Hosting.Redis.dll");

        File.WriteAllText(firstSourcePath, "redis");
        File.WriteAllText(secondSourcePath, "redis");

        var firstManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                firstSourcePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime")
        ],
        "{}",
        CancellationToken.None);

        var secondManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                secondSourcePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime")
        ],
        "{}",
        CancellationToken.None);

        Assert.NotEqual(firstManifest.ManifestFingerprint, secondManifest.ManifestFingerprint);
    }

    [Fact]
    public void ClosureManifest_ProjectLayoutManifestIgnoresPackageBackedEntries()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var firstPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-a");
        var secondPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-b");
        var projectRoot = workspace.WorkspaceRoot.CreateSubdirectory("project");
        var firstPackagePath = Path.Combine(firstPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var secondPackagePath = Path.Combine(secondPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var projectPath = Path.Combine(projectRoot.FullName, "MyIntegration.dll");

        File.WriteAllText(firstPackagePath, "redis");
        File.WriteAllText(secondPackagePath, "redis");
        File.WriteAllText(projectPath, "integration");

        var firstManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                firstPackagePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime"),
            new AppHostServerClosureSource(projectPath, "MyIntegration.dll")
        ],
        "{}",
        CancellationToken.None);

        var secondManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                secondPackagePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime"),
            new AppHostServerClosureSource(projectPath, "MyIntegration.dll")
        ],
        "{}",
        CancellationToken.None);

        Assert.NotEqual(firstManifest.ManifestFingerprint, secondManifest.ManifestFingerprint);
        Assert.Equal(firstManifest.ProjectLayoutFingerprint, secondManifest.ProjectLayoutFingerprint);
        Assert.Equal(firstManifest.GetProjectLayoutManifestLines(), secondManifest.GetProjectLayoutManifestLines());
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_ReusesProjectLayoutWhenOnlyPackageTimestampChanges()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var packageSourcePath = Path.Combine(workingDirectory, "integration-restore", "closure-sources", "Aspire.Hosting.Redis.dll");
            File.SetLastWriteTimeUtc(packageSourcePath, File.GetLastWriteTimeUtc(packageSourcePath).AddMinutes(5));

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);
            Assert.Equal(firstLayoutPath, server.SelectedProjectLayoutPath);
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    private static IReadOnlyList<IntegrationReference> CreateProjectReferenceIntegrations()
    {
        return
        [
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0"),
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        ];
    }

    private static PrebuiltAppHostServer CreateProjectReferenceServer(
        TemporaryWorkspace workspace,
        IReadOnlyDictionary<string, string> closureFiles,
        IReadOnlyList<string> projectReferenceAssemblyNames,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata = null,
        LayoutConfiguration? layout = null)
    {
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, _, _) =>
            {
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, projectReferenceAssemblyNames, packageMetadata);
                return 0;
            }
        };

        var nugetService = new BundleNuGetService(new NullLayoutDiscovery(), new LayoutProcessRunner(new TestProcessExecutionFactory()), new TestFeatures(), TestExecutionContextFactory.CreateTestContext(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);
        return new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            layout ?? new LayoutConfiguration(),
            nugetService,
            dotNetCliRunner,
            new TestDotNetSdkInstaller(),
            MockPackagingServiceFactory.Create(),
            TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    private static (PrebuiltAppHostServer Server, TestProcessExecutionFactory ExecutionFactory) CreatePackageReferenceServer(TemporaryWorkspace workspace)
    {
        return CreatePackageReferenceServer(workspace, MockPackagingServiceFactory.Create());
    }

    private static (PrebuiltAppHostServer Server, TestProcessExecutionFactory ExecutionFactory) CreatePackageReferenceServer(
        TemporaryWorkspace workspace,
        IPackagingService packagingService)
    {
        var layout = CreateBundleLayout(workspace);
        var executionFactory = new TestProcessExecutionFactory();
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);

        var server = new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            layout,
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            packagingService,
            TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        return (server, executionFactory);
    }

    private static LayoutConfiguration CreateBundleLayout(TemporaryWorkspace workspace)
    {
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(
                managedDirectory.FullName,
                BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);

        return new LayoutConfiguration { LayoutPath = layoutRoot.FullName };
    }

    private static void WriteClosureInputs(
        DirectoryInfo restoreDirectory,
        IReadOnlyDictionary<string, string> closureFiles,
        IReadOnlyList<string> projectReferenceAssemblyNames,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata = null)
    {
        var sourceRoot = restoreDirectory.CreateSubdirectory("closure-sources");
        var metadataLines = new List<string>();
        var sourcePaths = new List<string>();
        var targetPaths = new List<string>();

        foreach (var (relativePath, content) in closureFiles.OrderBy(static file => file.Key, StringComparer.Ordinal))
        {
            var sourcePath = Path.Combine(sourceRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var sourcePathDirectory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(sourcePathDirectory))
            {
                Directory.CreateDirectory(sourcePathDirectory);
            }

            if (!File.Exists(sourcePath) || File.ReadAllText(sourcePath) != content)
            {
                File.WriteAllText(sourcePath, content);
            }

            sourcePaths.Add(sourcePath);
            targetPaths.Add(relativePath.Replace('/', Path.DirectorySeparatorChar));
            metadataLines.Add(packageMetadata is not null && packageMetadata.TryGetValue(relativePath, out var package)
                ? $"{package.NuGetPackageId}|{package.NuGetPackageVersion}|{package.PathInPackage}|{package.AssetType}"
                : "|||");
        }

        WriteProjectAssetsFile(restoreDirectory, packageMetadata);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, PrebuiltAppHostServer.ClosureMetadataFileName), metadataLines);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, PrebuiltAppHostServer.ClosureSourcesFileName), sourcePaths);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, PrebuiltAppHostServer.ClosureTargetsFileName), targetPaths);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, PrebuiltAppHostServer.ProjectRefAssemblyNamesFileName), projectReferenceAssemblyNames);
    }

    private static IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)> CreatePackageMetadata()
    {
        return new Dictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/Aspire.Hosting.Redis.dll", "runtime")
        };
    }

    private static void WriteProjectAssetsFile(
        DirectoryInfo restoreDirectory,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata)
    {
        var objDirectory = restoreDirectory.CreateSubdirectory("obj");
        var libraries = packageMetadata is null
            ? string.Empty
            : string.Join(
                ",\n",
                packageMetadata.Values
                    .GroupBy(static package => (package.NuGetPackageId, package.NuGetPackageVersion))
                    .Select(static group => group.First())
                    .OrderBy(static package => package.NuGetPackageId, StringComparer.Ordinal)
                    .ThenBy(static package => package.NuGetPackageVersion, StringComparer.Ordinal)
                    .Select(static package => $$"""
                        "{{package.NuGetPackageId}}/{{package.NuGetPackageVersion}}": {
                          "sha512": "sha512-{{package.NuGetPackageId}}-{{package.NuGetPackageVersion}}",
                          "type": "package",
                          "path": "{{package.NuGetPackageId.ToLowerInvariant()}}/{{package.NuGetPackageVersion}}",
                          "files": [
                            "{{package.PathInPackage}}"
                          ]
                        }
                        """));

        var projectAssetsContent = $$"""
            {
              "libraries": {
            {{libraries}}
              }
            }
            """;
        File.WriteAllText(Path.Combine(objDirectory.FullName, "project.assets.json"), projectAssetsContent);
    }

    private static string GetWorkingDirectory(PrebuiltAppHostServer server)
    {
        return Assert.IsType<string>(
            typeof(PrebuiltAppHostServer)
                .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(server));
    }

    private static string GetArgumentValue(IReadOnlyList<string> arguments, string optionName)
    {
        var optionIndex = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], optionName, StringComparison.Ordinal))
            {
                optionIndex = i;
                break;
            }
        }

        Assert.True(optionIndex >= 0 && optionIndex < arguments.Count - 1, $"Option '{optionName}' was not found.");
        return arguments[optionIndex + 1];
    }

    [Fact]
    public void CreateStartInfo_SetsCliLogFilePathEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layout = CreateBundleLayout(workspace);
        var executionContext = TestExecutionContextFactory.CreateTestContext();
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            executionContext,
            NullLogger<BundleNuGetService>.Instance);

        var server = new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            layout,
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            MockPackagingServiceFactory.Create(),
            executionContext,
            NullLogger<PrebuiltAppHostServer>.Instance);

        var startInfo = server.CreateStartInfo(123);

        Assert.Equal(executionContext.LogFilePath, startInfo.Environment[KnownConfigNames.CliLogFilePath]);
    }

    private static string[] GetSourceArguments(IReadOnlyList<string> args)
    {
        var sources = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--source")
            {
                sources.Add(args[i + 1]);
            }
        }

        return [.. sources];
    }

    private static string[] GetPackagePatternsForSource(XDocument doc, string source)
    {
        return [.. doc.Descendants("packageSource")
            .Where(e => string.Equals(e.Attribute("key")?.Value, source, StringComparison.OrdinalIgnoreCase))
            .Elements("package")
            .Select(e => e.Attribute("pattern")?.Value)
            .OfType<string>()];
    }

    private static void DeleteWorkingDirectory(string workingDirectory)
    {
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class FixedLayoutDiscovery(LayoutConfiguration layout) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => layout;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => layout.GetComponentPath(component);

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }

}
