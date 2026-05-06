// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;

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
    public void GenerateIntegrationProjectFile_SetsOutDir()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0")
        };
        var projectRefs = new List<IntegrationReference>();

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/custom/output/path");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var outDir = doc.Descendants(ns + "OutDir").FirstOrDefault()?.Value;
        Assert.Equal("/custom/output/path", outDir);
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
            new TestConfigurationService(),
            Aspire.Cli.Tests.Mcp.TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var workingDirectory = Assert.IsType<string>(
            typeof(PrebuiltAppHostServer)
                .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(server));

        var rootDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "bundle-hosts");
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
            new TestConfigurationService(),
            Aspire.Cli.Tests.Mcp.TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var firstServer = CreateServer(firstAppHost.FullName);
        var secondServer = CreateServer(secondAppHost.FullName);

        var workingDirectoryField = typeof(PrebuiltAppHostServer)
            .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var firstWorkingDirectory = Assert.IsType<string>(workingDirectoryField.GetValue(firstServer));
        var secondWorkingDirectory = Assert.IsType<string>(workingDirectoryField.GetValue(secondServer));

        var bundleHostsRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "bundle-hosts");

        try
        {
            Assert.StartsWith(bundleHostsRoot, firstWorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(bundleHostsRoot, secondWorkingDirectory, StringComparison.OrdinalIgnoreCase);
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

    // PSM-guard cross-product tests (Ocean Wave 9 Part 2 follow-up / commit ed36ba4c30)
    // Guard predicate: channelName == IdentityChannel && !channelName.StartsWith("pr-")
    // When guard fires → TryCreateTemporaryNuGetConfigAsync returns null (no PSM for local-identity hive).
    // PR hives (pr-*) and non-matching channels always get PSM.

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_LocalIdentityChannel_NonPrName_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithChannel("local");
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_LocalIdentityChannel_PrChannelName_ReturnsConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithChannel("local");
        var server = CreateServerWithExplicitChannel(workspace, "pr-12345", executionContext);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "pr-12345");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_StableIdentityChannel_AnyChannel_ReturnsConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithChannel("stable");
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "local");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_DailyIdentityChannel_DailyChannel_ReturnsNull()
    {
        // channelName == IdentityChannel ("daily" == "daily") and not pr-* → guard fires → null.
        // This is the same predicate as local-on-local; the guard is not literal-"local"-only.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithChannel("daily");
        var server = CreateServerWithExplicitChannel(workspace, "daily", executionContext);

        var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "daily");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryCreateTemporaryNuGetConfig_PrIdentityChannel_PrChannelName_ReturnsConfig()
    {
        // A developer running a pr-16820 CLI (IdentityChannel = "pr") installs a pr-12345 hive.
        // "pr-12345" != "pr" → channelName != IdentityChannel → guard does not fire → PSM emits.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateContextWithChannel("pr");
        var server = CreateServerWithExplicitChannel(workspace, "pr-12345", executionContext);

        using var result = await InvokeTryCreateTemporaryNuGetConfigAsync(server, "pr-12345");

        Assert.NotNull(result);
    }

    private static CliExecutionContext CreateContextWithChannel(string channel) =>
        new(new DirectoryInfo(Path.GetTempPath()),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hives")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "cache")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "sdks")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "logs")),
            "test.log",
            channel: channel);

    private static PrebuiltAppHostServer CreateServerWithExplicitChannel(
        TemporaryWorkspace workspace,
        string channelName,
        CliExecutionContext executionContext)
    {
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var channel = PackageChannel.CreateExplicitChannel(
            channelName, PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache());
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
            new TestConfigurationService(),
            executionContext,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    private static async Task<TemporaryNuGetConfig?> InvokeTryCreateTemporaryNuGetConfigAsync(
        PrebuiltAppHostServer server, string channelName)
    {
        var method = typeof(PrebuiltAppHostServer).GetMethod(
            "TryCreateTemporaryNuGetConfigAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<TemporaryNuGetConfig?>)method.Invoke(server, [channelName, CancellationToken.None])!;
        return await task;
    }

    [Fact]
    public async Task ResolveChannelName_UsesProjectLocalAspireConfig_NotGlobalChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-new"
            }
            """);

        var configurationService = new TestConfigurationService
        {
            OnGetConfiguration = key => key == "channel" ? "pr-old" : null
        };

        var nugetService = new BundleNuGetService(new NullLayoutDiscovery(), new LayoutProcessRunner(new TestProcessExecutionFactory()), new TestFeatures(), TestExecutionContextFactory.CreateTestContext(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);
        var server = new PrebuiltAppHostServer(
            workspace.WorkspaceRoot.FullName,
            "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            Aspire.Cli.Tests.Mcp.MockPackagingServiceFactory.Create(),
            configurationService,
            Aspire.Cli.Tests.Mcp.TestExecutionContextFactory.CreateTestContext(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var method = typeof(PrebuiltAppHostServer).GetMethod("ResolveChannelName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var channel = (string?)method.Invoke(server, []);

        Assert.Equal("pr-new", channel);
    }

}
