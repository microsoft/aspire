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
    public void GenerateIntegrationProjectFile_WithEmptyAdditionalSources_DoesNotSetRestoreAdditionalProjectSources()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs", Enumerable.Empty<string>());
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var restoreSources = doc.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault();
        Assert.Null(restoreSources);
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
        PrebuiltAppHostServer server, string requestedChannel)
    {
        var method = typeof(PrebuiltAppHostServer).GetMethod(
            "TryCreateTemporaryNuGetConfigAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<TemporaryNuGetConfig?>)method.Invoke(server, [requestedChannel, CancellationToken.None])!;
        return await task;
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
            MockPackagingServiceFactory.Create(),
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
