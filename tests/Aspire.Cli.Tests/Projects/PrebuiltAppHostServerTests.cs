// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
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
            new TestConfigurationService(),
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

    [Fact]
    public async Task ResolveChannelNameAsync_UsesProjectLocalAspireConfig_NotGlobalChannel()
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
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var method = typeof(PrebuiltAppHostServer).GetMethod("ResolveChannelNameAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var channelTask = Assert.IsType<Task<string?>>(method.Invoke(server, [CancellationToken.None]));
        var channel = await channelTask;

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
            new TestConfigurationService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", []);

            Assert.True(result.Success);
            Assert.Null(server.SelectedSnapshotPath);

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
    public async Task PrepareAsync_WithProjectReferences_ReusesSnapshotWhenClosureIsUnchanged()
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

            var firstSnapshotPath = Assert.IsType<string>(server.SelectedSnapshotPath);

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);
            Assert.Equal(firstSnapshotPath, server.SelectedSnapshotPath);
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "snapshots", "items")));
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

            var snapshotPath = Assert.IsType<string>(server.SelectedSnapshotPath);
            var copiedLibs = Directory.GetFiles(Path.Combine(snapshotPath, "libs"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(snapshotPath, "libs"), path).Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["MyIntegration.dll"], copiedLibs);

            var probeManifestPath = Path.Combine(snapshotPath, "content", AppHostServerClosureSnapshot.IntegrationPackageProbeManifestFileName);
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
    public async Task PrepareAsync_WithProjectReferences_CreatesNewSnapshotWhenClosureChanges()
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

            var firstSnapshotPath = Assert.IsType<string>(server.SelectedSnapshotPath);

            closureFiles["MyIntegration.dll"] = "integration-v2-with-different-length";

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);

            var secondSnapshotPath = Assert.IsType<string>(server.SelectedSnapshotPath);
            Assert.NotEqual(firstSnapshotPath, secondSnapshotPath);
            Assert.True(Directory.Exists(firstSnapshotPath));
            Assert.True(Directory.Exists(secondSnapshotPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_DoesNotTouchLockedPreviousSnapshot()
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

            var firstSnapshotPath = Assert.IsType<string>(server.SelectedSnapshotPath);
            var lockedFilePath = Path.Combine(firstSnapshotPath, "libs", "MyIntegration.dll");

            using (var lockedFile = new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                closureFiles["MyIntegration.dll"] = "integration-v2-with-different-length";

                var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
                Assert.True(secondResult.Success);

                var secondSnapshotPath = Assert.IsType<string>(server.SelectedSnapshotPath);
                Assert.NotEqual(firstSnapshotPath, secondSnapshotPath);
                Assert.True(File.Exists(lockedFilePath));
                Assert.True(File.Exists(Path.Combine(secondSnapshotPath, "libs", "MyIntegration.dll")));
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
    public async Task PrepareAsync_WithProjectReferences_ReusesSnapshotWhenOnlyPackageTimestampChanges()
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

            var firstSnapshotPath = Assert.IsType<string>(server.SelectedSnapshotPath);
            var packageSourcePath = Path.Combine(workingDirectory, "integration-restore", "closure-sources", "Aspire.Hosting.Redis.dll");
            File.SetLastWriteTimeUtc(packageSourcePath, File.GetLastWriteTimeUtc(packageSourcePath).AddMinutes(5));

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);
            Assert.Equal(firstSnapshotPath, server.SelectedSnapshotPath);
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "snapshots", "items")));
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
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata = null)
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
            new LayoutConfiguration(),
            nugetService,
            dotNetCliRunner,
            new TestDotNetSdkInstaller(),
            MockPackagingServiceFactory.Create(),
            new TestConfigurationService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
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

    private static void DeleteWorkingDirectory(string workingDirectory)
    {
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

}
