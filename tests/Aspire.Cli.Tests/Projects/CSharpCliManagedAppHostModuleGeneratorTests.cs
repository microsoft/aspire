// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
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

        var generator = new CSharpCliManagedAppHostModuleGenerator(
            new TestFeatures().SetFeature(KnownFeatures.CSharpCliManagedAppHostEnabled, true),
            new TestPackagingService(),
            NullLogger<CSharpCliManagedAppHostModuleGenerator>.Instance);

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: null, CancellationToken.None);

        Assert.NotNull(moduleProjectFile);
        Assert.Equal(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Aspire.csproj"), moduleProjectFile.FullName);
        Assert.True(File.Exists(moduleProjectFile.FullName));

        var moduleProject = XDocument.Load(moduleProjectFile.FullName);
        Assert.Equal("Microsoft.NET.Sdk", moduleProject.Root!.Attribute("Sdk")!.Value);
        Assert.Contains(moduleProject.Root.Elements("Import"), e => e.Attribute("Project")?.Value == "Aspire.targets");

        var targetsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Aspire.targets");
        Assert.True(File.Exists(targetsPath));

        var targets = XDocument.Load(targetsPath);
        Assert.Contains(targets.Descendants("PackageReference"), e =>
            e.Attribute("Include")?.Value == "Example.Package" &&
            e.Attribute("Version")?.Value == "1.2.3");
        Assert.Contains(targets.Descendants("ProjectReference"), e =>
            e.Attribute("Include")?.Value == projectReferenceFile.FullName &&
            e.Element("IsAspireProjectResource")?.Value == "false");
        Assert.Contains(targets.Descendants("Target"), e =>
            e.Attribute("Name")?.Value == "FailDirectDotnetForCliManagedAppHost" &&
            e.Attribute("BeforeTargets")?.Value == "Build;Publish" &&
            e.Attribute("Condition")?.Value == "'$(AspireCliManagedAppHostBuild)' != 'true'");

        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Directory.Build.props")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Directory.Build.targets")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "Directory.Packages.props")));
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
        var generator = new CSharpCliManagedAppHostModuleGenerator(
            new TestFeatures().SetFeature(KnownFeatures.CSharpCliManagedAppHostEnabled, true),
            new TestPackagingService(),
            NullLogger<CSharpCliManagedAppHostModuleGenerator>.Instance);

        await generator.TryGenerateAsync(appHostFile, config, appHostDirectory, packageSourceOverride: null, CancellationToken.None);

        var targetsPath = Path.Combine(appHostDirectory.FullName, ".aspire", "modules", "Aspire.targets");
        var targets = XDocument.Load(targetsPath);
        var packageReferences = targets.Descendants("PackageReference").ToArray();
        var projectReferences = targets.Descendants("ProjectReference").ToArray();

        Assert.Empty(packageReferences);
        Assert.Contains(projectReferences, e => e.Attribute("Include")?.Value == hostingProject.FullName);
        Assert.Contains(projectReferences, e => e.Attribute("Include")?.Value == redisProject.FullName);
        Assert.Contains(projectReferences, e =>
            e.Attribute("Include")?.Value == dashboardProject.FullName &&
            e.Element("ReferenceOutputAssembly")?.Value == "false");
    }

    [Fact]
    public async Task TryGenerateAsyncAddsChannelAndSourceOverrideRestoreSources()
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
                    new TestFeatures());
                return Task.FromResult<IEnumerable<PackageChannel>>([daily]);
            }
        };
        var generator = new CSharpCliManagedAppHostModuleGenerator(
            new TestFeatures().SetFeature(KnownFeatures.CSharpCliManagedAppHostEnabled, true),
            packagingService,
            NullLogger<CSharpCliManagedAppHostModuleGenerator>.Instance);

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, config, workspace.WorkspaceRoot, packageSourceOverride: "/tmp/aspire-pr-hive/packages", CancellationToken.None);

        Assert.NotNull(moduleProjectFile);
        Assert.Equal("daily", packagingService.LastRequestedChannelName);

        var moduleProject = XDocument.Load(moduleProjectFile.FullName);
        var sources = moduleProject.Root!
            .Element("PropertyGroup")!
            .Element("RestoreAdditionalProjectSources")!
            .Value
            .Split(';');
        var restoreConfigFile = moduleProject.Root!
            .Element("PropertyGroup")!
            .Element("RestoreConfigFile")!
            .Value;

        Assert.Equal(
            ["/tmp/aspire-pr-hive/packages", "https://example.invalid/daily/aspire", "https://example.invalid/daily/all"],
            sources);
        Assert.Equal(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules", "nuget.config"), restoreConfigFile);

        var nugetConfig = XDocument.Load(restoreConfigFile);
        Assert.Equal(["/tmp/aspire-pr-hive/packages", "https://example.invalid/daily/all"], GetPackageSources(nugetConfig));
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(nugetConfig, "/tmp/aspire-pr-hive/packages"));
        Assert.Equal(["*"], GetPackagePatternsForSource(nugetConfig, "https://example.invalid/daily/all"));
    }

    [Fact]
    public async Task TryGenerateAsyncReturnsNullWhenFeatureIsDisabled()
    {
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var appHostFile = CreateCliManagedAppHost(workspace.WorkspaceRoot);
        var generator = new CSharpCliManagedAppHostModuleGenerator(
            new TestFeatures(),
            new TestPackagingService(),
            NullLogger<CSharpCliManagedAppHostModuleGenerator>.Instance);

        var moduleProjectFile = await generator.TryGenerateAsync(appHostFile, CancellationToken.None);

        Assert.Null(moduleProjectFile);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules")));
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
        var packageSources = doc.Root!
            .Element("packageSourceMapping")!
            .Elements("packageSource");
        var packageSource = packageSources.Single(e => e.Attribute("key")?.Value == source);

        return packageSource
            .Elements("package")
            .Select(e => e.Attribute("pattern")!.Value)
            .ToArray();
    }
}
