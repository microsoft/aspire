// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class CSharpCliManagedAppHostModuleGeneratorTests : IDisposable
{
    private readonly ITestOutputHelper _outputHelper;

    public CSharpCliManagedAppHostModuleGeneratorTests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;
        AspireRepositoryDetector.ResetCache();
    }

    public void Dispose()
    {
        AspireRepositoryDetector.ResetCache();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TryGenerateAsyncCreatesModuleProjectAndTargets()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var projectReferenceFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Integration", "Integration.csproj"));
        projectReferenceFile.Directory!.Create();
        await File.WriteAllTextAsync(projectReferenceFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var config = new AspireConfigFile
        {
            SdkVersion = "13.2.0",
            Packages = new Dictionary<string, string>
            {
                ["Example.Package"] = "1.2.3",
                ["Local.Integration"] = "Integration/Integration.csproj"
            }
        };

        var generator = CreateGenerator(workspace);

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: null, CancellationToken.None);

        Assert.NotNull(moduleProjectFile);
        Assert.Equal(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Aspire.csproj"), moduleProjectFile.FullName);
        Assert.True(File.Exists(moduleProjectFile.FullName));

        var moduleProject = XDocument.Load(moduleProjectFile.FullName);
        Assert.Equal("Microsoft.NET.Sdk", moduleProject.Root!.Attribute("Sdk")!.Value);
        var propertyGroup = moduleProject.Root.Element("PropertyGroup")!;
        Assert.Equal("net10.0", propertyGroup.Element("TargetFramework")?.Value);
        Assert.Equal("false", propertyGroup.Element("EnableDefaultItems")?.Value);
        Assert.Equal("false", propertyGroup.Element("EnableNETAnalyzers")?.Value);
        Assert.Equal("false", propertyGroup.Element("GenerateDocumentationFile")?.Value);
        Assert.Equal("false", propertyGroup.Element("IsPackable")?.Value);
        Assert.Equal("false", propertyGroup.Element("IsPublishable")?.Value);
        var restoreDir = Path.Combine(
            IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(appHostFile.Directory!).FullName,
            IntegrationClosureBuilder.IntegrationRestoreFolderName);
        Assert.Null(propertyGroup.Element("BaseOutputPath"));
        Assert.Null(propertyGroup.Element("BaseIntermediateOutputPath"));
        Assert.Null(propertyGroup.Element("MSBuildProjectExtensionsPath"));
        Assert.Equal("false", propertyGroup.Element("ProduceReferenceAssembly")?.Value);
        Assert.DoesNotContain(moduleProject.Root.Elements("Import"), e => e.Attribute("Project")?.Value == "Aspire.targets");
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Aspire.targets")));
        Assert.Contains(moduleProject.Descendants("PackageReference"), e =>
            e.Attribute("Include")?.Value == "Example.Package" &&
            e.Attribute("Version")?.Value == "1.2.3");
        Assert.Contains(moduleProject.Descendants("ProjectReference"), e =>
            e.Attribute("Include")?.Value == projectReferenceFile.FullName &&
            e.Element("IsAspireProjectResource")?.Value == "false");
        Assert.Contains(moduleProject.Descendants("Target"), e =>
            e.Attribute("Name")?.Value == "FailDirectDotnetForCliManagedAppHost" &&
            e.Attribute("BeforeTargets")?.Value == "Build;Publish" &&
            e.Attribute("Condition")?.Value == "'$(AspireCliManagedAppHostBuild)' != 'true'");

        var appHostBuildPropsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "AppHost.Directory.Build.props");
        var appHostBuildTargetsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "AppHost.Directory.Build.targets");
        Assert.True(File.Exists(appHostBuildPropsPath));
        Assert.True(File.Exists(appHostBuildTargetsPath));

        var generatedAppHostProjectPath = Path.ChangeExtension(appHostFile.FullName, ".csproj");
        var appHostBuildProps = XDocument.Load(appHostBuildPropsPath);
        var appHostBuildTargets = XDocument.Load(appHostBuildTargetsPath);
        var projectCondition = $"\"$(MSBuildProjectFullPath)\" == \"{CliPathHelper.EscapeMSBuildConditionStringLiteral(generatedAppHostProjectPath)}\"";
        Assert.Null(appHostBuildProps.Root!.Attribute("Sdk"));
        Assert.DoesNotContain(appHostBuildProps.Root!.Elements("Import"), e => e.Attribute("Project")?.Value?.Contains("Directory.Build.props", StringComparison.Ordinal) == true);

        Assert.Null(appHostBuildTargets.Root!.Attribute("Sdk"));
        Assert.Contains(appHostBuildTargets.Root.Elements("ItemGroup"), e =>
            e.Attribute("Condition")?.Value == projectCondition &&
            e.Element("ProjectReference") is { } projectReference &&
            projectReference.Attribute("Update")?.Value == "@(ProjectReference)" &&
            projectReference.Attribute("GlobalPropertiesToRemove")?.Value == "%(ProjectReference.GlobalPropertiesToRemove);DirectoryBuildPropsPath;DirectoryBuildTargetsPath");

        var appHostPropertyGroup = appHostBuildProps.Root.Elements("PropertyGroup")
            .Single(e => e.Attribute("Condition")?.Value == projectCondition);
        Assert.Equal(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "build", "apphost", "bin") + Path.DirectorySeparatorChar,
            appHostPropertyGroup.Element("BaseOutputPath")?.Value);
        Assert.Equal(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "build", "apphost", "obj") + Path.DirectorySeparatorChar,
            appHostPropertyGroup.Element("BaseIntermediateOutputPath")?.Value);
        Assert.Equal("$(BaseIntermediateOutputPath)", appHostPropertyGroup.Element("MSBuildProjectExtensionsPath")?.Value);
        Assert.Equal("false", appHostPropertyGroup.Element("ManagePackageVersionsCentrally")?.Value);
        Assert.Equal("false", appHostPropertyGroup.Element("CentralPackageTransitivePinningEnabled")?.Value);

        Assert.Contains(appHostBuildProps.Descendants("PackageReference"), e =>
            e.Attribute("Include")?.Value == "Example.Package" &&
            e.Attribute("Version")?.Value == "1.2.3");
        Assert.Contains(appHostBuildProps.Descendants("ProjectReference"), e =>
            e.Attribute("Include")?.Value == projectReferenceFile.FullName &&
            e.Element("ReferenceOutputAssembly")?.Value == "true");

        var moduleDirectoryBuildPropsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Directory.Build.props");
        Assert.True(File.Exists(moduleDirectoryBuildPropsPath));
        var moduleDirectoryBuildProps = XDocument.Load(moduleDirectoryBuildPropsPath);
        var moduleDirectoryBuildPropertyGroup = moduleDirectoryBuildProps.Root!.Element("PropertyGroup")!;
        Assert.Equal(
            Path.Combine(restoreDir, "bin") + Path.DirectorySeparatorChar,
            moduleDirectoryBuildPropertyGroup.Element("BaseOutputPath")?.Value);
        Assert.Equal(
            Path.Combine(restoreDir, "obj") + Path.DirectorySeparatorChar,
            moduleDirectoryBuildPropertyGroup.Element("BaseIntermediateOutputPath")?.Value);
        Assert.Equal("$(BaseIntermediateOutputPath)", moduleDirectoryBuildPropertyGroup.Element("MSBuildProjectExtensionsPath")?.Value);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Directory.Build.targets")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Directory.Packages.props")));
    }

    [Fact]
    public async Task TryGenerateAsyncUsesCliIdentitySdkVersionWhenConfigDoesNotSpecifyOne()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var config = new AspireConfigFile
        {
            Packages = new Dictionary<string, string>
            {
                ["Example.Package"] = ""
            }
        };
        var generator = CreateGenerator(workspace, identitySdkVersion: "42.0.0-dev");

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: null, CancellationToken.None);

        Assert.NotNull(moduleProjectFile);
        var moduleProject = XDocument.Load(moduleProjectFile.FullName);
        Assert.Contains(moduleProject.Descendants("PackageReference"), e =>
            e.Attribute("Include")?.Value == "Example.Package" &&
            e.Attribute("Version")?.Value == "42.0.0-dev");
    }

    [Fact]
    public async Task TryGenerateAsyncRemovesStaleGeneratedNuGetConfigsWhenNoMappingIsResolved()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var modulesDirectory = workspace.WorkspaceRoot.CreateSubdirectory(".aspire").CreateSubdirectory("modules");
        var nuGetConfigPath = Path.Combine(modulesDirectory.FullName, "nuget.config");
        var policyConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "NuGet.Config");
        await File.WriteAllTextAsync(nuGetConfigPath, """
            <configuration>
              <packageSources>
                <add key="source-override" value="/tmp/source-override" />
              </packageSources>
            </configuration>
            """);
        await File.WriteAllTextAsync(policyConfigPath, "<configuration />");
        var generator = CreateGenerator(workspace);

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, new AspireConfigFile(), workspace.WorkspaceRoot, packageSourceOverride: null, CancellationToken.None);

        Assert.NotNull(moduleProjectFile);
        Assert.False(File.Exists(nuGetConfigPath));
        Assert.False(File.Exists(policyConfigPath));
        var directoryBuildProps = XDocument.Load(Path.Combine(modulesDirectory.FullName, "Directory.Build.props"));
        Assert.Equal(workspace.WorkspaceRoot.FullName, directoryBuildProps.Descendants("RestoreRootConfigDirectory").Single().Value);
        Assert.Equal(string.Empty, directoryBuildProps.Descendants("RestoreConfigFile").Single().Value);
    }

    [Fact]
    public async Task TryGenerateAsyncUsesStableGlobalPackagesFolderForStagingChannel()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var aspireHomeDirectory = workspace.WorkspaceRoot.CreateSubdirectory(".aspire-home");
        var stagingSource = "https://example.invalid/staging";
        var config = new AspireConfigFile
        {
            Channel = PackageChannelNames.Staging,
            SdkVersion = "13.2.0"
        };
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var staging = PackageChannel.CreateExplicitChannel(
                    PackageChannelNames.Staging,
                    PackageChannelQuality.Both,
                    [new PackageMapping("Aspire*", stagingSource)],
                    new FakeNuGetPackageCache(),
                    new TestFeatures(),
                    NullLogger.Instance,
                    configureGlobalPackagesFolder: true);
                return Task.FromResult<IEnumerable<PackageChannel>>([staging]);
            }
        };
        var generator = CreateGenerator(
            workspace,
            packagingService,
            aspireHomeDirectory: aspireHomeDirectory);

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: null, CancellationToken.None);

        Assert.NotNull(moduleProjectFile);
        var directoryBuildProps = XDocument.Load(Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "modules",
            "Directory.Build.props"));
        var globalPackagesFolder = directoryBuildProps.Descendants("RestorePackagesPath").Single().Value;
        Assert.StartsWith(
            CliPathHelper.GetStagingNuGetPackagesDirectory(aspireHomeDirectory),
            globalPackagesFolder,
            StringComparison.Ordinal);
        var policyConfig = XDocument.Load(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "NuGet.Config"));
        Assert.Equal(
            globalPackagesFolder,
            policyConfig.Root!.Element("config")!.Elements("add").Single(e => e.Attribute("key")?.Value == "globalPackagesFolder").Attribute("value")!.Value);
    }

    [Fact]
    public async Task TryGenerateAsyncEscapesAppHostPathInGeneratedMSBuildConditions()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("O'Brien");
        var appHostFile = CreateCliManagedAppHost(appHostDirectory);
        var generator = CreateGenerator(workspace);

        await generator.TryGenerateAsync(appHostFile, new AspireConfigFile(), appHostDirectory, packageSourceOverride: null, CancellationToken.None);

        var generatedAppHostProjectPath = Path.ChangeExtension(appHostFile.FullName, ".csproj");
        var expectedCondition = $"\"$(MSBuildProjectFullPath)\" == \"{CliPathHelper.EscapeMSBuildConditionStringLiteral(generatedAppHostProjectPath)}\"";
        var appHostBuildProps = XDocument.Load(Path.Combine(appHostDirectory.FullName, ".aspire", "modules", "AppHost.Directory.Build.props"));
        var appHostBuildTargets = XDocument.Load(Path.Combine(appHostDirectory.FullName, ".aspire", "modules", "AppHost.Directory.Build.targets"));

        Assert.Contains(appHostBuildProps.Root!.Elements("PropertyGroup"), e => e.Attribute("Condition")?.Value == expectedCondition);
        Assert.Contains(appHostBuildTargets.Root!.Elements("ItemGroup"), e => e.Attribute("Condition")?.Value == expectedCondition);
    }

    [Fact]
    public async Task TryGenerateAsyncUsesRepositoryProjectReferencesForAspireHostingPackages()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("playground").CreateSubdirectory("TestApp");
        var appHostFile = CreateCliManagedAppHost(appHostDirectory);
        var repoRoot = workspace.WorkspaceRoot.FullName;
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "Aspire.slnx"), string.Empty);
        var hostingProject = CreateRepositoryProject(workspace.WorkspaceRoot, "Aspire.Hosting");
        var redisProject = CreateRepositoryProject(workspace.WorkspaceRoot, "Aspire.Hosting.Redis");
        var dashboardProject = CreateRepositoryProject(workspace.WorkspaceRoot, "Aspire.Dashboard");
        var config = new AspireConfigFile
        {
            SdkVersion = "13.2.0",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "13.2.1"
            }
        };
        var generator = CreateGenerator(workspace);

        await generator.TryGenerateAsync(appHostFile, config, appHostDirectory, packageSourceOverride: null, CancellationToken.None);

        var moduleProjectPath = Path.Combine(appHostDirectory.FullName, ".aspire", "modules", "Aspire.csproj");
        var moduleProject = XDocument.Load(moduleProjectPath);
        var packageReferences = moduleProject.Descendants("PackageReference").ToArray();
        var projectReferences = moduleProject.Descendants("ProjectReference").ToArray();

        Assert.Empty(packageReferences);
        Assert.Contains(projectReferences, e => e.Attribute("Include")?.Value == hostingProject.FullName);
        Assert.Contains(projectReferences, e => e.Attribute("Include")?.Value == redisProject.FullName);
        Assert.Contains(projectReferences, e =>
            e.Attribute("Include")?.Value == dashboardProject.FullName &&
            e.Element("ReferenceOutputAssembly")?.Value == "false");
    }

    [Fact]
    public async Task TryGenerateAsyncUsesNuGetConfigForChannelAndSourceOverride()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var config = new AspireConfigFile
        {
            Channel = "daily",
            SdkVersion = "13.2.0"
        };
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var daily = PackageChannel.CreateExplicitChannel(
                    "daily",
                    PackageChannelQuality.Both,
                    [
                        new PackageMapping("Aspire*", "https://example.invalid/daily/aspire"),
                        new PackageMapping("*", "https://example.invalid/daily/all")
                    ],
                    new FakeNuGetPackageCache(),
                    new TestFeatures(),
                    NullLogger.Instance);
                return Task.FromResult<IEnumerable<PackageChannel>>([daily]);
            }
        };
        var generator = CreateGenerator(workspace, packagingService);

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: "/tmp/aspire-pr-hive/packages", CancellationToken.None);

        Assert.NotNull(moduleProjectFile);
        Assert.Equal("daily", packagingService.LastRequestedChannelName);

        var moduleProject = XDocument.Load(moduleProjectFile.FullName);
        Assert.Equal(string.Empty, moduleProject.Root!
            .Element("PropertyGroup")!
            .Element("RestoreAdditionalProjectSources")!
            .Value);
        var restoreConfigFile = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "NuGet.Config");

        var appHostBuildPropsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "AppHost.Directory.Build.props");
        var appHostBuildProps = XDocument.Load(appHostBuildPropsPath);
        Assert.Equal(
            Path.GetDirectoryName(restoreConfigFile),
            appHostBuildProps.Descendants("RestoreRootConfigDirectory").Single().Value);

        var nugetConfig = XDocument.Load(restoreConfigFile);
        Assert.Equal(["/tmp/aspire-pr-hive/packages", "https://example.invalid/daily/all"], GetPackageSources(nugetConfig));
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(nugetConfig, "/tmp/aspire-pr-hive/packages"));
        Assert.Equal(["*"], GetPackagePatternsForSource(nugetConfig, "https://example.invalid/daily/all"));
    }

    [Fact]
    public async Task TryGenerateAsyncUsesSourceOverrideWithoutResolvingChannelsWhenChannelIsUnset()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var config = new AspireConfigFile
        {
            SdkVersion = "13.2.0"
        };
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channels should not be resolved.")
        };
        var generator = CreateGenerator(workspace, packagingService);

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: "/tmp/aspire-pr-hive/packages", CancellationToken.None);

        Assert.NotNull(moduleProjectFile);

        var moduleProject = XDocument.Load(moduleProjectFile.FullName);
        Assert.Equal(string.Empty, moduleProject.Root!
            .Element("PropertyGroup")!
            .Element("RestoreAdditionalProjectSources")!
            .Value);
        var restoreConfigFile = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "NuGet.Config");

        var nugetConfig = XDocument.Load(restoreConfigFile);
        Assert.Equal(["/tmp/aspire-pr-hive/packages", PackageSources.NuGetOrg], GetPackageSources(nugetConfig));
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(nugetConfig, "/tmp/aspire-pr-hive/packages"));
        Assert.Equal(["*"], GetPackagePatternsForSource(nugetConfig, PackageSources.NuGetOrg));
    }

    [Fact]
    public async Task TryGenerateAsyncUsesNuGetServiceIndexOverrideForSourceOverrideFallback()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var config = new AspireConfigFile
        {
            SdkVersion = "13.2.0"
        };
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channels should not be resolved.")
        };
        var generator = CreateGenerator(
            workspace,
            packagingService,
            nugetServiceIndexOverride: "http://localhost:5400/v3/index.json");

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: "/tmp/aspire-pr-hive/packages", CancellationToken.None);

        Assert.NotNull(moduleProjectFile);

        var restoreConfigFile = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "NuGet.Config");

        var nugetConfig = XDocument.Load(restoreConfigFile);
        Assert.Equal(["/tmp/aspire-pr-hive/packages", "http://localhost:5400/v3/index.json"], GetPackageSources(nugetConfig));
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(nugetConfig, "/tmp/aspire-pr-hive/packages"));
        Assert.Equal(["*"], GetPackagePatternsForSource(nugetConfig, "http://localhost:5400/v3/index.json"));
    }

    [Fact]
    public async Task TryGenerateAsyncThrowsWhenStagingChannelIsUnavailable()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var config = new AspireConfigFile
        {
            Channel = PackageChannelNames.Staging,
            SdkVersion = "13.2.0"
        };
        var packagingService = new TestPackagingService
        {
            GetStagingChannelUnavailableReasonCallback = () => "Staging is not available."
        };
        var generator = CreateGenerator(workspace, packagingService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: null, CancellationToken.None));

        Assert.Equal("Staging is not available.", exception.Message);
    }

    [Fact]
    public async Task TryGenerateWithRestoreConfigurationAsyncReturnsSensitiveSourcesWithoutPersistingPackageSourceHints()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var sensitiveSource = "https://user:password@example.test/v3/index.json";
        var packageSourceOverride = "https://packages.example.test/v3/index.json";
        var generator = CreateGenerator(
            workspace,
            nugetSettings: CreateNuGetSettings(sensitiveSourceValues: [sensitiveSource]));

        var result = await generator.TryGenerateWithRestoreConfigurationAsync(
            appHostFile,
            new AspireConfigFile(),
            workspace.WorkspaceRoot,
            packageSourceOverride,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(sensitiveSource, result.SensitiveSources);
        Assert.Contains(packageSourceOverride, Assert.IsType<string>(result.IntegrationPackageSources));
        var appHostBuildProps = XDocument.Load(Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".aspire",
            "modules",
            CSharpCliManagedAppHostModuleGenerator.AppHostBuildPropsFileName));
        Assert.Empty(appHostBuildProps.Descendants(PrebuiltAppHostServer.IntegrationPackageSourcesPropertyName));
    }

    private static FileInfo CreateCliManagedAppHost(DirectoryInfo directory)
    {
        var appHostPath = Path.Combine(directory.FullName, "apphost.cs");
        File.WriteAllText(appHostPath, """
            #:project .aspire/modules/Aspire.csproj

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        return new FileInfo(appHostPath);
    }

    private static FileInfo CreateRepositoryProject(DirectoryInfo repoRoot, string projectName)
    {
        var projectDirectory = repoRoot.CreateSubdirectory("src").CreateSubdirectory(projectName);
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, $"{projectName}.csproj"));
        File.WriteAllText(projectFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        return projectFile;
    }

    private static CSharpCliManagedAppHostModuleGenerator CreateGenerator(
        TemporaryWorkspace workspace,
        IPackagingService? packagingService = null,
        string identitySdkVersion = "13.4.0",
        DirectoryInfo? aspireHomeDirectory = null,
        string? nugetServiceIndexOverride = null,
        NuGetSettingsInfo? nugetSettings = null)
    {
        var layoutRoot = workspace.WorkspaceRoot.CreateSubdirectory(
            $".bundle-{Guid.NewGuid():N}");
        var layout = new LayoutConfiguration
        {
            LayoutPath = layoutRoot.FullName
        };
        var managedPath = layout.GetManagedPath()!;
        Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
        File.WriteAllText(managedPath, "test");

        var processExecutionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) =>
            {
                if (args.Length >= 2 && args[0] == "nuget" && args[1] == "write-config")
                {
                    WriteNuGetConfigOverlay(args);
                }
            },
            AttemptCallback = (_, _) => (
                0,
                JsonSerializer.Serialize(nugetSettings ?? CreateNuGetSettings()))
        };
        var bundleNuGetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(processExecutionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance)
        {
            SourceIdentityKeyFactory = static () => new byte[NuGetSourceIdentity.KeySizeInBytes]
        };
        var executionContext = new CliExecutionContext(
            workspace.WorkspaceRoot,
            workspace.WorkspaceRoot,
            workspace.WorkspaceRoot,
            workspace.WorkspaceRoot,
            workspace.WorkspaceRoot,
            Path.Combine(workspace.WorkspaceRoot.FullName, "test.log"),
            PackageChannelNames.Stable,
            aspireHomeDirectory: aspireHomeDirectory ?? workspace.WorkspaceRoot.CreateSubdirectory(".aspire-home"),
            identityVersion: identitySdkVersion,
            nugetServiceIndexOverride: nugetServiceIndexOverride);

        return new CSharpCliManagedAppHostModuleGenerator(
            packagingService ?? new TestPackagingService(),
            bundleNuGetService,
            executionContext,
            NullLogger<CSharpCliManagedAppHostModuleGenerator>.Instance);
    }

    private static NuGetSettingsInfo CreateNuGetSettings(string[]? sensitiveSourceValues = null)
    {
        return new NuGetSettingsInfo(
            ConfigPaths: [],
            CacheIdentity: "test-cache",
            Sources: [],
            SensitiveSourceValues: sensitiveSourceValues ?? [],
            PackageSourceMappingEnabled: false,
            PackageSourceMappings: [],
            DisabledPackageSourceKeys: [],
            ReservedPackageSourceKeys: [],
            SourceIdentityKey: new byte[NuGetSourceIdentity.KeySizeInBytes]);
    }

    private static void WriteNuGetConfigOverlay(string[] args)
    {
        static string GetArgumentValue(string[] values, string name)
        {
            var index = Array.IndexOf(values, name);
            return index >= 0 && index + 1 < values.Length
                ? values[index + 1]
                : throw new InvalidDataException($"Missing '{name}'.");
        }

        var request = JsonSerializer.Deserialize<NuGetConfigOverlayRequest>(
            File.ReadAllText(GetArgumentValue(args, "--request")))
            ?? throw new InvalidDataException("The NuGet configuration request was empty.");
        var configuration = new XElement("configuration");
        if (request.Sources.Length > 0)
        {
            configuration.Add(new XElement(
                "packageSources",
                request.Sources.Select(source => new XElement(
                    "add",
                    new XAttribute("key", source.Key),
                    new XAttribute("value", source.Source)))));
        }
        if (request.PackageSourceMappings.Length > 0)
        {
            configuration.Add(new XElement(
                "packageSourceMapping",
                request.PackageSourceMappings.Select(mapping => new XElement(
                    "packageSource",
                    new XAttribute("key", mapping.SourceKey),
                    mapping.Patterns.Select(pattern => new XElement(
                        "package",
                        new XAttribute("pattern", pattern)))))));
        }
        if (request.GlobalPackagesFolder is not null)
        {
            configuration.Add(new XElement(
                "config",
                new XElement(
                    "add",
                    new XAttribute("key", "globalPackagesFolder"),
                    new XAttribute("value", request.GlobalPackagesFolder))));
        }

        new XDocument(configuration).Save(GetArgumentValue(args, "--output"));
    }

    private sealed class FixedLayoutDiscovery(LayoutConfiguration layout) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => layout;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => layout.GetComponentPath(component);

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }

    private static string[] GetPackageSources(XDocument doc)
    {
        return doc.Root!
            .Element("packageSources")!
            .Elements("add")
            .Select(e => e.Attribute("value")!.Value)
            .ToArray();
    }

    private static string[] GetPackagePatternsForSource(XDocument doc, string source)
    {
        var sourceKey = doc.Root!
            .Element("packageSources")!
            .Elements("add")
            .Single(element => PackageSourceIdentity.Comparer.Equals(
                element.Attribute("value")?.Value,
                source))
            .Attribute("key")!
            .Value;
        var packageSource = doc.Root!
            .Element("packageSourceMapping")!
            .Elements("packageSource")
            .Single(element => string.Equals(
                element.Attribute("key")?.Value,
                sourceKey,
                StringComparison.OrdinalIgnoreCase));

        return packageSource
            .Elements("package")
            .Select(e => e.Attribute("pattern")!.Value)
            .ToArray();
    }
}
