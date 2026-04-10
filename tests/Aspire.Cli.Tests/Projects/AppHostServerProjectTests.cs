// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class AppHostServerProjectTests(ITestOutputHelper outputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(outputHelper);

    public void Dispose()
    {
        _workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    private DotNetBasedAppHostServerProject CreateProject(string? appPath = null)
    {
        appPath ??= _workspace.WorkspaceRoot.FullName;
        var runner = new TestDotNetCliRunner();
        var packagingService = new MockPackagingService();
        var configurationService = new TestConfigurationService();
        var logger = NullLogger<DotNetBasedAppHostServerProject>.Instance;

        // Generate socket path same way as factory
        var socketPath = "test.sock";

        // Use workspace root as repo root for testing
        var repoRoot = _workspace.WorkspaceRoot.FullName;

        return new DotNetBasedAppHostServerProject(appPath, socketPath, repoRoot, runner, packagingService, configurationService, logger);
    }

    [Fact]
    public async Task CreateProjectFiles_AppSettingsJson_MatchesSnapshot()
    {
        // Arrange
        var project = CreateProject();
        var packages = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.1.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.PostgreSQL", "13.1.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.1.0")
        };

        // Act
        await project.CreateProjectFilesAsync(packages).DefaultTimeout();

        // Assert
        var appSettingsPath = Path.Combine(project.ProjectModelPath, "appsettings.json");
        var content = await File.ReadAllTextAsync(appSettingsPath);

        await Verify(content, extension: "json")
            .UseFileName("AppHostServerProject_AppSettingsJson");
    }

    [Fact]
    public async Task CreateProjectFiles_ProgramCs_MatchesSnapshot()
    {
        // Arrange
        var project = CreateProject();
        var packages = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")
        };

        // Act
        await project.CreateProjectFilesAsync(packages).DefaultTimeout();

        // Assert
        var programCsPath = Path.Combine(project.ProjectModelPath, "Program.cs");
        var content = await File.ReadAllTextAsync(programCsPath);

        // Use .txt extension to avoid compilation of snapshot file
        await Verify(content, extension: "txt")
            .UseFileName("AppHostServerProject_ProgramCs");
    }

    [Fact]
    public async Task CreateProjectFiles_GeneratesProgramCs()
    {
        // Arrange
        var project = CreateProject();
        var packages = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")
        };

        // Act
        await project.CreateProjectFilesAsync(packages).DefaultTimeout();

        // Assert
        var programCs = Path.Combine(project.ProjectModelPath, "Program.cs");
        Assert.True(File.Exists(programCs));

        var content = await File.ReadAllTextAsync(programCs);
        Assert.Contains("RemoteHostServer.RunAsync", content);
    }

    [Fact]
    public async Task CreateProjectFiles_GeneratesAppSettingsJson_WithAtsAssemblies()
    {
        // Arrange
        var project = CreateProject();
        var packages = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.1.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.1.0")
        };

        // Act
        await project.CreateProjectFilesAsync(packages).DefaultTimeout();

        // Assert
        var appSettingsPath = Path.Combine(project.ProjectModelPath, "appsettings.json");
        Assert.True(File.Exists(appSettingsPath));

        var content = await File.ReadAllTextAsync(appSettingsPath);
        Assert.Contains("AtsAssemblies", content);
        Assert.Contains("Aspire.Hosting", content);
        Assert.Contains("Aspire.Hosting.Redis", content);
        Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript", content);
    }

    [Fact]
    public async Task CreateProjectFiles_CopiesAppSettingsToOutput()
    {
        // Arrange
        var project = CreateProject();
        var packages = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")
        };

        // Act
        var (projectPath, _) = await project.CreateProjectFilesAsync(packages).DefaultTimeout();

        // Assert
        var doc = XDocument.Load(projectPath);

        var noneElement = doc.Descendants("None")
            .FirstOrDefault(e => e.Attribute("Include")?.Value == "appsettings.json");

        Assert.NotNull(noneElement);
        Assert.Equal("PreserveNewest", noneElement.Attribute("CopyToOutputDirectory")?.Value);
    }

    [Fact]
    public void DefaultSdkVersion_ReturnsValidVersion()
    {
        // Act
        var version = DotNetBasedAppHostServerProject.DefaultSdkVersion;

        // Assert
        Assert.NotNull(version);
        Assert.NotEmpty(version);
        // Should not contain '+' (commit hash should be stripped)
        Assert.DoesNotContain("+", version);
    }

    [Fact]
    public void ProjectModelPath_IsStableForSameAppPath()
    {
        // Arrange
        var appPath = _workspace.WorkspaceRoot.FullName;

        // Act
        var project1 = CreateProject(appPath);
        var project2 = CreateProject(appPath);

        // Assert - same app path should result in same project model path
        Assert.Equal(project1.ProjectModelPath, project2.ProjectModelPath);
    }

    [Fact]
    public void ProjectModelPath_UsesUserAspireDirectory()
    {
        // Arrange
        var project = CreateProject();
        var normalizedAppPath = Path.GetFullPath(project.AppDirectoryPath);
        normalizedAppPath = new Uri(normalizedAppPath).LocalPath;
        normalizedAppPath = OperatingSystem.IsWindows() ? normalizedAppPath.ToLowerInvariant() : normalizedAppPath;

        var pathHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedAppPath));
        var pathDir = Convert.ToHexString(pathHash)[..12].ToLowerInvariant();
        var expectedRoot = Path.Combine(CliPathHelper.GetAspireHomeDirectory(), "hosts");
        var expectedPath = Path.Combine(expectedRoot, pathDir);
        var parentDirectory = Path.GetDirectoryName(project.ProjectModelPath);
        var isSafeToDelete = string.Equals(project.ProjectModelPath, expectedPath, StringComparison.OrdinalIgnoreCase) &&
            parentDirectory is not null &&
            string.Equals(parentDirectory, expectedRoot, StringComparison.OrdinalIgnoreCase);

        try
        {
            // Assert
            Assert.Equal(expectedPath, project.ProjectModelPath);
        }
        finally
        {
            if (isSafeToDelete && Directory.Exists(project.ProjectModelPath))
            {
                Directory.Delete(project.ProjectModelPath, recursive: true);
            }
        }
    }

    [Fact]
    public void UserSecretsId_IsStableForSameAppPath()
    {
        // Arrange
        var appPath = _workspace.WorkspaceRoot.FullName;

        // Act
        var project1 = CreateProject(appPath);
        var project2 = CreateProject(appPath);

        // Assert - same app path should result in same user secrets ID
        Assert.Equal(project1.UserSecretsId, project2.UserSecretsId);
    }

    /// <summary>
    /// Regression test for channel switching bug.
    /// When a project has a channel configured in aspire.config.json (project-local),
    /// the NuGet.config should use that channel's hive path, NOT the global config channel.
    /// 
    /// Bug scenario:
    /// 1. User runs `aspire update` and selects "pr-new" channel
    /// 2. UpdatePackagesAsync saves channel="pr-new" to project-local aspire.config.json
    /// 3. BuildAndGenerateSdkAsync calls CreateProjectFilesAsync
    /// 4. BUG: CreateProjectFilesAsync reads channel from GLOBAL config (returns "pr-old")
    /// 5. NuGet.config is generated with pr-old hive path instead of pr-new
    /// 6. Build fails because packages are in pr-new hive but NuGet.config points to pr-old
    /// </summary>
    [Fact]
    public async Task CreateProjectFiles_NuGetConfig_UsesProjectLocalChannel_NotGlobalChannel_MatchesSnapshot()
    {
        // Arrange
        var appPath = _workspace.WorkspaceRoot.FullName;

        // Create two PR hive directories to simulate having multiple PR builds
        var hivesDir = _workspace.WorkspaceRoot.CreateSubdirectory("hives");
        var prOldHive = hivesDir.CreateSubdirectory("pr-old");
        var prNewHive = hivesDir.CreateSubdirectory("pr-new");

        // Create project-local aspire.config.json with channel="pr-new"
        // This simulates what happens after `aspire update` saves the selected channel
        var aspireConfigPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-new",
                "sdk": {
                    "version": "13.1.0"
                }
            }
            """);

        // Configure global config to return "pr-old" (the WRONG channel)
        // This simulates a stale global config that hasn't been updated
        var configurationService = new TestConfigurationService
        {
            OnGetConfiguration = key => key == "channel" ? "pr-old" : null
        };

        // Create a packaging service that returns explicit channels for both PR hives
        var packagingService = new MockPackagingServiceWithExplicitChannels(
            prOldHive.FullName,
            prNewHive.FullName);

        var runner = new TestDotNetCliRunner();
        
        // Use a real logger to capture debug output for diagnostics
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddXunit(outputHelper);
        });
        var logger = loggerFactory.CreateLogger<DotNetBasedAppHostServerProject>();

        // Use a workspace-local ProjectModelPath for test isolation
        var projectModelPath = Path.Combine(appPath, ".aspire_server");
        var project = new DotNetBasedAppHostServerProject(appPath, "test.sock", appPath, runner, packagingService, configurationService, logger, projectModelPath);

        var packages = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.AppHost", "13.1.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.1.0")
        };

        // Act
        await project.CreateProjectFilesAsync(packages).DefaultTimeout();

        // Dump workspace directory tree for debugging
        outputHelper.WriteLine("=== Workspace Directory Tree ===");
        DumpDirectoryTree(appPath, outputHelper);
        outputHelper.WriteLine("================================");

        // Also dump ProjectModelPath content
        outputHelper.WriteLine($"=== ProjectModelPath ({project.ProjectModelPath}) ===");
        if (Directory.Exists(project.ProjectModelPath))
        {
            DumpDirectoryTree(project.ProjectModelPath, outputHelper);
        }
        else
        {
            outputHelper.WriteLine("  (directory does not exist)");
        }
        outputHelper.WriteLine("================================");

        // Assert - verify the csproj uses RestoreAdditionalProjectSources with the correct channel sources
        var projectFilePath = Path.Combine(project.ProjectModelPath, DotNetBasedAppHostServerProject.ProjectFileName);

        Assert.True(File.Exists(projectFilePath), $"Project file should be created at {projectFilePath}");

        var projectContent = await File.ReadAllTextAsync(projectFilePath);
        var projectDoc = XDocument.Parse(projectContent);
        var restoreSources = projectDoc.Descendants("RestoreAdditionalProjectSources").FirstOrDefault()?.Value;

        // Should contain the pr-new hive path (project-local channel), NOT pr-old (global config)
        Assert.NotNull(restoreSources);
        Assert.Contains(prNewHive.FullName, restoreSources);
        Assert.DoesNotContain(prOldHive.FullName, restoreSources);
    }

    /// <summary>
    /// Mock packaging service that returns explicit PR channels with specific hive paths.
    /// Used to test that the correct channel is selected based on project-local settings.
    /// </summary>
    private sealed class MockPackagingServiceWithExplicitChannels : IPackagingService
    {
        private readonly string _prOldHivePath;
        private readonly string _prNewHivePath;

        public MockPackagingServiceWithExplicitChannels(string prOldHivePath, string prNewHivePath)
        {
            _prOldHivePath = prOldHivePath;
            _prNewHivePath = prNewHivePath;
        }

        public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
        {
            var nugetCache = new FakeNuGetPackageCache();

            // Create explicit channels for both PR hives
            var prOldChannel = PackageChannel.CreateExplicitChannel("pr-old", PackageChannelQuality.Prerelease, new[]
            {
                new PackageMapping("Aspire*", _prOldHivePath),
                new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
            }, nugetCache);

            var prNewChannel = PackageChannel.CreateExplicitChannel("pr-new", PackageChannelQuality.Prerelease, new[]
            {
                new PackageMapping("Aspire*", _prNewHivePath),
                new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
            }, nugetCache);

            var implicitChannel = PackageChannel.CreateImplicitChannel(nugetCache);

            return Task.FromResult<IEnumerable<PackageChannel>>(new[] { implicitChannel, prOldChannel, prNewChannel });
        }
    }

    private sealed class FakeNuGetPackageCache : INuGetPackageCache
    {
        public Task<IEnumerable<NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<NuGetPackageCli>>([]);

        public Task<IEnumerable<NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<NuGetPackageCli>>([]);

        public Task<IEnumerable<NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<NuGetPackageCli>>([]);

        public Task<IEnumerable<NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<NuGetPackageCli>>([]);
    }

    private static void DumpDirectoryTree(string path, ITestOutputHelper output, string indent = "")
    {
        var dirInfo = new DirectoryInfo(path);
        output.WriteLine($"{indent}{dirInfo.Name}/");
        
        foreach (var file in dirInfo.GetFiles())
        {
            output.WriteLine($"{indent}  {file.Name}");
        }
        
        foreach (var dir in dirInfo.GetDirectories())
        {
            DumpDirectoryTree(dir.FullName, output, indent + "  ");
        }
    }
}
