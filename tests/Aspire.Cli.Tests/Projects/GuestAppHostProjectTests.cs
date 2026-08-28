// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Net.Sockets;
using System.Text;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Processes;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Aspire.Cli.Tests.Projects;

public class GuestAppHostProjectTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly IConfiguration _configuration;
    private readonly ProfilingTelemetry _profilingTelemetry;

    public GuestAppHostProjectTests(ITestOutputHelper outputHelper)
    {
        _workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        _configuration = new ConfigurationBuilder().Build();
        _profilingTelemetry = new ProfilingTelemetry(_configuration);
    }

    public void Dispose()
    {
        _profilingTelemetry.Dispose();
        _workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PruneObsoleteGeneratedFilesAsync_RemovesFilesTheGeneratorNoLongerProduces()
    {
        var outputPath = Path.Combine(_workspace.WorkspaceRoot.FullName, ".aspire", "modules");
        Directory.CreateDirectory(Path.Combine(outputPath, "aspire"));
        var kept = Path.Combine(outputPath, "aspire", "Kept.java");
        var obsolete = Path.Combine(outputPath, "aspire", "RemovedResource.java");
        await File.WriteAllTextAsync(kept, "class Kept { }");
        await File.WriteAllTextAsync(obsolete, "class RemovedResource { }");

        // The first generation records what it wrote.
        await GuestAppHostProject.PruneObsoleteGeneratedFilesAsync(
            outputPath,
            [Path.Combine("aspire", "Kept.java"), Path.Combine("aspire", "RemovedResource.java")],
            CancellationToken.None);

        // Dropping the package that produced RemovedResource.java means the generator stops emitting it.
        // javac compiles everything under the source root, so leaving it behind fails the build with a
        // reference to a type that no longer exists.
        await GuestAppHostProject.PruneObsoleteGeneratedFilesAsync(
            outputPath,
            [Path.Combine("aspire", "Kept.java")],
            CancellationToken.None);

        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(obsolete));
    }

    [Fact]
    public async Task PruneObsoleteGeneratedFilesAsync_LeavesFilesTheGeneratorNeverWrote()
    {
        var outputPath = Path.Combine(_workspace.WorkspaceRoot.FullName, ".aspire", "modules");
        Directory.CreateDirectory(outputPath);
        var handWritten = Path.Combine(outputPath, "HandWritten.java");
        await File.WriteAllTextAsync(handWritten, "class HandWritten { }");

        // Only files a previous generation recorded are eligible, so nothing Aspire did not write is
        // ever deleted - including on the very first run, where there is no manifest at all.
        await GuestAppHostProject.PruneObsoleteGeneratedFilesAsync(outputPath, ["Generated.java"], CancellationToken.None);
        await GuestAppHostProject.PruneObsoleteGeneratedFilesAsync(outputPath, ["Generated.java"], CancellationToken.None);

        Assert.True(File.Exists(handWritten));
    }

    [Fact]
    public async Task WriteGeneratedFileAsync_WhenContentIsUnchanged_LeavesTimestampAlone()
    {
        var filePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "Generated.java");
        await File.WriteAllTextAsync(filePath, "class Generated { }");
        var originalWriteTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(filePath, originalWriteTime);

        var written = await GuestAppHostProject.WriteGeneratedFileAsync(filePath, "class Generated { }", preserveUnchangedFiles: true, CancellationToken.None);

        Assert.False(written);
        // The timestamp is the contract: every downstream incremental build decides what to recompile
        // from it, so regenerating identical content must not look like a change.
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(filePath));
    }

    [Fact]
    public async Task WriteGeneratedFileAsync_WhenContentDiffers_WritesFile()
    {
        var filePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "Generated.java");
        await File.WriteAllTextAsync(filePath, "class Generated { }");
        File.SetLastWriteTimeUtc(filePath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var written = await GuestAppHostProject.WriteGeneratedFileAsync(filePath, "class Generated { int x; }", preserveUnchangedFiles: true, CancellationToken.None);

        Assert.True(written);
        Assert.Equal("class Generated { int x; }", await File.ReadAllTextAsync(filePath));
        Assert.True(File.GetLastWriteTimeUtc(filePath) > new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task WriteGeneratedFileAsync_WhenFileIsMissing_WritesFile()
    {
        var filePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "New.java");

        var written = await GuestAppHostProject.WriteGeneratedFileAsync(filePath, "class New { }", preserveUnchangedFiles: true, CancellationToken.None);

        Assert.True(written);
        Assert.Equal("class New { }", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task WriteGeneratedFileAsync_WhenNotPreservingUnchangedFiles_RewritesIdenticalContent()
    {
        var filePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "aspire_app.py");
        await File.WriteAllTextAsync(filePath, "def add_redis(): ...");
        var originalWriteTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(filePath, originalWriteTime);

        var written = await GuestAppHostProject.WriteGeneratedFileAsync(filePath, "def add_redis(): ...", preserveUnchangedFiles: false, CancellationToken.None);

        // Languages that install the generated sources into an environment (uv/pip for Python)
        // rebuild off the source timestamp, so an unchanged file must still be rewritten or the
        // stale install is silently reused.
        Assert.True(written);
        Assert.True(File.GetLastWriteTimeUtc(filePath) > originalWriteTime);
    }

    [Fact]
    public void JavaIsTheOnlyLanguageThatPreservesUnchangedGeneratedFiles()
    {
        var languages = DefaultLanguageDiscovery.AllLanguages;

        Assert.Equal(
            ["Java"],
            languages.Where(language => language.PreserveUnchangedGeneratedFiles).Select(language => language.CodeGenerator));
    }

    [Fact]
    public void AspireJsonConfiguration_LoadOrCreate_SetsDefaultSdkVersion()
    {
        // Arrange
        var directory = _workspace.WorkspaceRoot.FullName;

        // Act
        var config = AspireJsonConfiguration.LoadOrCreate(directory, "13.1.0");

        // Assert
        Assert.Equal("13.1.0", config.SdkVersion);
    }

    [Fact]
    public void AspireJsonConfiguration_LoadOrCreate_PreservesExistingSdkVersion()
    {
        // Arrange - create settings.json with existing SDK version
        var settingsDir = _workspace.CreateDirectory(".aspire");
        var settingsPath = Path.Combine(settingsDir.FullName, "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "sdkVersion": "12.0.0",
                "language": "typescript"
            }
            """);

        // Act
        var config = AspireJsonConfiguration.LoadOrCreate(_workspace.WorkspaceRoot.FullName, "13.1.0");

        // Assert - should preserve existing version, not override with default
        Assert.Equal("12.0.0", config.SdkVersion);
    }

    [Fact]
    public void AspireJsonConfiguration_Save_UpdatesSdkVersion()
    {
        // Arrange - create initial settings.json
        var settingsDir = _workspace.CreateDirectory(".aspire");
        var settingsPath = Path.Combine(settingsDir.FullName, "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "sdkVersion": "12.0.0",
                "language": "typescript",
                "packages": {
                    "Aspire.Hosting.Redis": "12.0.0"
                }
            }
            """);

        // Act - load, update SDK version, and save
        var config = AspireJsonConfiguration.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(config);
        config.SdkVersion = "13.1.0";
        config.Save(_workspace.WorkspaceRoot.FullName);

        // Assert - reload and verify
        var reloaded = AspireJsonConfiguration.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        Assert.Equal("13.1.0", reloaded.SdkVersion);
        Assert.Equal("typescript", reloaded.Language);
        Assert.NotNull(reloaded.Packages);
        Assert.Equal("12.0.0", reloaded.Packages["Aspire.Hosting.Redis"]);
    }

    [Fact]
    public void AspireJsonConfiguration_AddOrUpdatePackage_AddsNewPackage()
    {
        // Arrange
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.1.0",
            Language = "typescript"
        };

        // Act
        config.AddOrUpdatePackage("Aspire.Hosting.Redis", "13.1.0");

        // Assert
        Assert.NotNull(config.Packages);
        Assert.Single(config.Packages);
        Assert.Equal("13.1.0", config.Packages["Aspire.Hosting.Redis"]);
    }

    [Fact]
    public void AspireJsonConfiguration_AddOrUpdatePackage_UpdatesExistingPackage()
    {
        // Arrange
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.1.0",
            Language = "typescript",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "12.0.0"
            }
        };

        // Act
        config.AddOrUpdatePackage("Aspire.Hosting.Redis", "13.1.0");

        // Assert
        Assert.NotNull(config.Packages);
        Assert.Single(config.Packages);
        Assert.Equal("13.1.0", config.Packages["Aspire.Hosting.Redis"]);
    }

    [Fact]
    public void AspireJsonConfiguration_GetIntegrationReferences_IncludesBasePackages()
    {
        // Arrange
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.1.0",
            Language = "typescript",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "13.1.0"
            }
        };

        // Act
        var refs = config.GetIntegrationReferences("13.1.0", "/tmp").ToList();

        // Assert - should include base package (Aspire.Hosting) plus explicit packages
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting" && r.Version == "13.1.0" && !r.IsProjectReference);
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting.Redis" && r.Version == "13.1.0" && !r.IsProjectReference);
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void AspireJsonConfiguration_GetIntegrationReferences_WithNoExplicitPackages_ReturnsBasePackagesOnly()
    {
        // Arrange
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.1.0",
            Language = "typescript"
        };

        // Act
        var refs = config.GetIntegrationReferences("13.1.0", "/tmp").ToList();

        // Assert - should include base package only (Aspire.Hosting)
        Assert.Single(refs);
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting" && r.Version == "13.1.0");
    }

    [Fact]
    public void AspireJsonConfiguration_GetIntegrationReferences_WithEmptyVersion_UsesFallbackVersion()
    {
        // Arrange
        var config = new AspireJsonConfiguration
        {
            Language = "typescript",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = string.Empty
            }
        };

        // Act
        var refs = config.GetIntegrationReferences("13.1.0", "/tmp").ToList();

        // Assert
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting" && r.Version == "13.1.0");
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting.Redis" && r.Version == "13.1.0");
    }

    [Fact]
    public void AspireJsonConfiguration_GetIntegrationReferences_WithConfiguredSdkVersion_ReturnsConfiguredVersions()
    {
        // Arrange
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.1.0",
            Language = "typescript",
            Channel = "daily",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "13.1.0"
            }
        };

        // Act
        var refs = config.GetIntegrationReferences("13.1.0", "/tmp").ToList();

        // Assert
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting" && r.Version == "13.1.0");
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting.Redis" && r.Version == "13.1.0");
    }

    [Fact]
    public void AspireJsonConfiguration_GetIntegrationReferences_WithProjectReference_ReturnsProjectRef()
    {
        // Arrange
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.1.0",
            Language = "typescript",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "13.1.0",
                ["Aspire.Hosting.MyCustom"] = "../src/Aspire.Hosting.MyCustom/Aspire.Hosting.MyCustom.csproj"
            }
        };

        // Act
        var refs = config.GetIntegrationReferences("13.1.0", "/home/user/app").ToList();

        // Assert
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting" && r.IsPackageReference);
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting.Redis" && r.IsPackageReference);
        var projectRef = Assert.Single(refs, r => r.IsProjectReference);
        Assert.Equal("Aspire.Hosting.MyCustom", projectRef.Name);
        Assert.Null(projectRef.Version);
        Assert.NotNull(projectRef.ProjectPath);
        Assert.EndsWith(".csproj", projectRef.ProjectPath);
    }

    [Fact]
    public void AspireJsonConfiguration_Save_PreservesExtensionData()
    {
        // Arrange - create settings.json with extra properties
        var settingsDir = _workspace.CreateDirectory(".aspire");
        var settingsPath = Path.Combine(settingsDir.FullName, "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "sdkVersion": "13.1.0",
                "language": "typescript",
                "features": {
                    "experimental": true
                },
                "customProperty": "customValue"
            }
            """);

        // Act - load, modify, and save
        var config = AspireJsonConfiguration.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(config);
        config.SdkVersion = "13.2.0";
        config.Save(_workspace.WorkspaceRoot.FullName);

        // Assert - reload and verify extension data is preserved
        var json = File.ReadAllText(settingsPath);
        Assert.Contains("features", json);
        Assert.Contains("experimental", json);
        Assert.Contains("customProperty", json);
        Assert.Contains("customValue", json);
    }

    [Fact]
    public async Task AspireJsonConfiguration_MatchesSnapshot()
    {
        // Arrange - create a full settings.json
        var config = new AspireJsonConfiguration
        {
            Schema = "https://json.schemastore.org/aspire-settings.json",
            AppHostPath = "apphost.ts",
            Language = "typescript",
            SdkVersion = "13.1.0",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "13.1.0",
                ["Aspire.Hosting.PostgreSQL"] = "13.1.0"
            }
        };

        // Act
        config.Save(_workspace.WorkspaceRoot.FullName);

        // Assert
        var settingsPath = AspireJsonConfiguration.GetFilePath(_workspace.WorkspaceRoot.FullName);
        var content = await File.ReadAllTextAsync(settingsPath);

        await Verify(content, extension: "json")
            .UseFileName("AspireJsonConfiguration_SettingsJson");
    }

    [Fact]
    public void GetServerEnvironmentVariables_ParsesLaunchSettingsWithComments()
    {
        var project = CreateGuestAppHostProject();

        var propertiesDir = _workspace.CreateDirectory("Properties");
        var launchSettingsPath = Path.Combine(propertiesDir.FullName, "launchSettings.json");
        File.WriteAllText(launchSettingsPath, """
            {
              "profiles": {
                "https": {
                  "commandName": "Project",
                  "applicationUrl": "https://localhost:16319;http://localhost:16320",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development",
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:17269",
                    // This is a commented-out environment variable
                    //"ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL": "https://localhost:17269",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:17269"
                  }
                }
              }
            }
            """);

        var envVars = project.GetServerEnvironmentVariables(_workspace.WorkspaceRoot);

        Assert.Equal("https://localhost:16319;http://localhost:16320", envVars[KnownAspNetCoreConfigNames.Urls]);
        Assert.Equal("Development", envVars[KnownAspNetCoreConfigNames.Environment]);
        Assert.Equal("https://localhost:17269", envVars["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://localhost:17269", envVars["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        Assert.False(envVars.ContainsKey("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"));
    }

    [Fact]
    public void GetServerEnvironmentVariables_UsesRequestedDefaultEnvironment()
    {
        var envVars = GuestAppHostProject.GetServerEnvironmentVariables(
            launchProfileEnvironmentVariables: null,
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(envVars.ContainsKey(KnownAspNetCoreConfigNames.Environment));
    }

    [Fact]
    public void GetServerEnvironmentVariables_IgnoresLaunchProfileEnvironmentVariablesWhenRequested()
    {
        var envVars = GuestAppHostProject.GetServerEnvironmentVariables(
            launchProfileEnvironmentVariables: new Dictionary<string, string>
            {
                [KnownAspNetCoreConfigNames.Urls] = "https://localhost:16319;http://localhost:16320",
                [KnownAspNetCoreConfigNames.Environment] = "Development",
                [KnownAspNetCoreConfigNames.DotNetEnvironment] = "Development",
                ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "https://localhost:17269",
                ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "https://localhost:18269"
            },
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            includeLaunchProfileEnvironmentVariables: false,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(envVars.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("https://localhost:16319;http://localhost:16320", envVars[KnownAspNetCoreConfigNames.Urls]);
        Assert.Equal("https://localhost:17269", envVars["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://localhost:18269", envVars["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        Assert.False(envVars.ContainsKey("ASPIRE_ENVIRONMENT"));
    }

    [Fact]
    public void GetServerEnvironmentVariables_EnvironmentArgumentTakesPrecedenceOverLaunchProfileEnvironmentVariables()
    {
        var envVars = GuestAppHostProject.GetServerEnvironmentVariables(
            launchProfileEnvironmentVariables: new Dictionary<string, string>
            {
                [KnownAspNetCoreConfigNames.Urls] = "https://localhost:16319;http://localhost:16320",
                ["ASPIRE_ENVIRONMENT"] = "Development",
                [KnownAspNetCoreConfigNames.Environment] = "Development",
                [KnownAspNetCoreConfigNames.DotNetEnvironment] = "Development",
            },
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inheritedEnvironmentVariables: new Dictionary<string, string?>(),
            args: ["--environment", "Staging"]);

        Assert.Equal("Staging", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.Equal("Development", envVars[KnownAspNetCoreConfigNames.Environment]);
        Assert.Equal("Development", envVars["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_MergesLaunchProfileContextAndAdditionalEnvironmentVariables()
    {
        var project = CreateGuestAppHostProject();

        var aspireConfigPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(aspireConfigPath, """
            {
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:16319;http://localhost:16320",
                  "environmentVariables": {
                    "ASPIRE_ENVIRONMENT": "Staging",
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:17269",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:18269"
                  }
                }
              }
            }
            """);

        var envVars = project.CreateGuestEnvironmentVariables(
            _workspace.WorkspaceRoot,
            new Dictionary<string, string>
            {
                ["CUSTOM_CONTEXT_VARIABLE"] = "context",
                [KnownAspNetCoreConfigNames.Urls] = "http://context"
            },
            new Dictionary<string, string>
            {
                ["SSL_CERT_DIR"] = "/tmp/certs"
            });

        Assert.Equal("context", envVars["CUSTOM_CONTEXT_VARIABLE"]);
        Assert.Equal("https://localhost:16319;http://localhost:16320", envVars[KnownAspNetCoreConfigNames.Urls]);
        Assert.Equal("Staging", envVars["ASPIRE_ENVIRONMENT"]);
        Assert.Equal("Staging", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(envVars.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("https://localhost:17269", envVars["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://localhost:18269", envVars["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        Assert.Equal("/tmp/certs", envVars["SSL_CERT_DIR"]);
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_IgnoresLaunchProfileEnvironmentVariablesWhenRequested()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>(),
            launchProfileEnvironmentVariables: new Dictionary<string, string>
            {
                [KnownAspNetCoreConfigNames.Urls] = "https://localhost:16319;http://localhost:16320",
                ["ASPIRE_ENVIRONMENT"] = "Development",
                [KnownAspNetCoreConfigNames.Environment] = "Development",
                [KnownAspNetCoreConfigNames.DotNetEnvironment] = "Development",
                ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "https://localhost:17269",
                ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "https://localhost:18269"
            },
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            includeLaunchProfileEnvironmentVariables: false,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(envVars.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("https://localhost:16319;http://localhost:16320", envVars[KnownAspNetCoreConfigNames.Urls]);
        Assert.Equal("https://localhost:17269", envVars["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://localhost:18269", envVars["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        Assert.False(envVars.ContainsKey("ASPIRE_ENVIRONMENT"));
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_ForwardsAppHostArgumentsForGuestsThatCannotReadArgv()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>(),
            launchProfileEnvironmentVariables: null,
            inheritedEnvironmentVariables: new Dictionary<string, string?>(),
            args: ["--operation", "publish", "--step", "publish", "--output-path", "/tmp/out dir"]);

        Assert.Equal(
            "--operation\npublish\n--step\npublish\n--output-path\n/tmp/out dir",
            envVars["ASPIRE_APPHOST_ARGS"]);
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_DoesNotForwardAppHostArgumentsWhenThereAreNone()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>(),
            launchProfileEnvironmentVariables: null,
            inheritedEnvironmentVariables: new Dictionary<string, string?>(),
            args: []);

        Assert.False(envVars.ContainsKey("ASPIRE_APPHOST_ARGS"));
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_EnvironmentArgumentTakesPrecedenceOverLaunchProfileEnvironmentVariables()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>(),
            launchProfileEnvironmentVariables: new Dictionary<string, string>
            {
                ["ASPIRE_ENVIRONMENT"] = "Development",
                [KnownAspNetCoreConfigNames.Environment] = "Development",
                [KnownAspNetCoreConfigNames.DotNetEnvironment] = "Development",
            },
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inheritedEnvironmentVariables: new Dictionary<string, string?>(),
            args: ["--environment", "Staging"]);

        Assert.Equal("Staging", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.Equal("Development", envVars[KnownAspNetCoreConfigNames.Environment]);
        Assert.Equal("Development", envVars["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_InheritedAspireEnvironmentOverridesDefaultEnvironment()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>(),
            launchProfileEnvironmentVariables: null,
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inheritedEnvironmentVariables: new Dictionary<string, string?>
            {
                [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Staging"
            });

        Assert.Equal("Staging", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(envVars.ContainsKey(KnownAspNetCoreConfigNames.Environment));
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_DotnetEnvironmentTakesPrecedenceOverAspireEnvironment()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>
            {
                [KnownAspNetCoreConfigNames.DotNetEnvironment] = "Production",
                [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Staging"
            },
            launchProfileEnvironmentVariables: null,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.False(envVars.ContainsKey(KnownAspNetCoreConfigNames.Environment));
        Assert.Equal("Staging", envVars["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_AspireEnvironmentTakesPrecedenceOverAspNetCoreEnvironment()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>
            {
                [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Testing",
                [KnownAspNetCoreConfigNames.Environment] = "Staging"
            },
            launchProfileEnvironmentVariables: null,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Testing", envVars[KnownAspNetCoreConfigNames.DotNetEnvironment]);
        Assert.Equal("Staging", envVars[KnownAspNetCoreConfigNames.Environment]);
        Assert.Equal("Testing", envVars["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void ConvertGeneratedFilesForLegacyTypeScriptAppHost_UsesTsFilesAndJsSpecifiers()
    {
        var files = new Dictionary<string, string>
        {
            ["aspire.mts"] = "import { refExpr } from './base.mjs';\n// aspire.mts",
            ["base.mts"] = "export type { MarshalledHandle } from './transport.mjs';\n// base.mts",
            ["transport.mts"] = "// transport.mts"
        };

        var convertedFiles = GuestAppHostProject.ConvertGeneratedFilesForLegacyTypeScriptAppHost(files);

        Assert.Equal(["aspire.ts", "base.ts", "transport.ts"], convertedFiles.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("import { refExpr } from './base.js';\n// aspire.ts", convertedFiles["aspire.ts"]);
        Assert.Equal("export type { MarshalledHandle } from './transport.js';\n// base.ts", convertedFiles["base.ts"]);
        Assert.Equal("// transport.ts", convertedFiles["transport.ts"]);
    }

    /// <summary>
    /// Regression test for issue #17077: <c>aspire update</c> must not leave
    /// <c>aspire.config.json</c> advanced to newer package versions when guest SDK
    /// regeneration fails.
    /// </summary>
    /// <remarks>
    /// The test drives <see cref="GuestAppHostProject.UpdatePackagesAsync"/> through the
    /// code path that detects updates, then expects the call to throw from
    /// <c>BuildAndGenerateSdkAsync</c> because <see cref="TestAppHostServerProjectFactory.CreateAsync"/>
    /// throws. The on-disk config should still contain the original versions.
    /// </remarks>
    [Fact]
    public async Task UpdatePackagesAsync_WhenRegenerationFails_DoesNotMutateConfig()
    {
        var configPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, """
            {
              "sdk": { "version": "1.0.0" },
              "packages": { "Aspire.Hosting": "1.0.0" }
            }
            """);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        var fakeCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, packageId, _, _, _, _, _) =>
                Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                [
                    new Aspire.Shared.NuGetPackageCli { Id = packageId, Version = "2.0.0", Source = "test" }
                ])
        };

        var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures(), NullLogger.Instance);

        var interactionService = new TestInteractionService
        {
            ConfirmCallback = (_, _) => true
        };

        var project = CreateGuestAppHostProject(
            interactionService: interactionService,
            identityChannel: "pr-99999");

        var context = new UpdatePackagesContext
        {
            AppHostFile = new FileInfo(appHostPath),
            Channel = implicitChannel,
            ConfirmBinding = PromptBinding.CreateDefault<bool>(false),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null),
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => project.UpdatePackagesAsync(context, CancellationToken.None));

        var reloaded = AspireConfigFile.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        Assert.Equal("1.0.0", reloaded.SdkVersion);
        Assert.NotNull(reloaded.Packages);
        Assert.Equal("1.0.0", reloaded.Packages["Aspire.Hosting"]);
        Assert.Null(reloaded.Channel);
    }

    [Fact]
    public async Task AddPackageAsync_WhenRegenerationFails_DoesNotMutateConfig()
    {
        var configPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, """
            {
              "sdk": { "version": "1.0.0" },
              "packages": { "Aspire.Hosting": "1.0.0" }
            }
            """);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        var factory = new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (appPath, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeFailingAppHostServerProject(appPath))
        };

        var project = CreateGuestAppHostProject(appHostServerProjectFactory: factory);

        var result = await project.AddPackageAsync(
            new AddPackageContext
            {
                AppHostFile = new FileInfo(appHostPath),
                PackageId = "Aspire.Hosting.Redis",
                PackageVersion = "2.0.0",
            },
            CancellationToken.None);

        Assert.False(result);

        var reloaded = AspireConfigFile.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        Assert.Equal("1.0.0", reloaded.SdkVersion);
        Assert.NotNull(reloaded.Packages);
        Assert.Equal("1.0.0", reloaded.Packages["Aspire.Hosting"]);
        Assert.False(reloaded.Packages.ContainsKey("Aspire.Hosting.Redis"));
    }

    [Fact]
    public async Task FindAndStopRunningInstanceAsync_CleansUpDeadPidSocketAndReturnsNoRunningInstance()
    {
        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        var factory = new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (appPath, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeFailingAppHostServerProject(appPath))
        };

        var project = CreateGuestAppHostProject(appHostServerProjectFactory: factory);
        var socketPath = CreateMatchingSocketFile(_workspace.WorkspaceRoot.FullName, int.MaxValue - 1);

        var result = await project.FindAndStopRunningInstanceAsync(
            new FileInfo(appHostPath),
            _workspace.WorkspaceRoot,
            CancellationToken.None);

        Assert.Equal(RunningInstanceResult.NoRunningInstance, result);
        Assert.False(File.Exists(socketPath));
    }

    [Fact]
    public async Task UpdatePackagesAsync_ExplicitStableChannel_WhenRegenerationFails_DoesNotMutateConfig()
    {
        var configPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, """
            {
              "sdk": { "version": "1.0.0" },
              "channel": "staging",
              "packages": { "Aspire.Hosting": "1.0.0" }
            }
            """);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        var stableCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, packageId, _, _, _, _, _) =>
                Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                [
                    new Aspire.Shared.NuGetPackageCli { Id = packageId, Version = "2.0.0", Source = "stable" }
                ])
        };

        var stableChannel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Stable,
            PackageChannelQuality.Both,
            [new PackageMapping("Aspire.*", "stable")],
            stableCache,
            features: new TestFeatures(), NullLogger.Instance);

        var interactionService = new TestInteractionService
        {
            ConfirmCallback = (_, _) => true
        };

        var project = CreateGuestAppHostProject(interactionService: interactionService);

        var context = new UpdatePackagesContext
        {
            AppHostFile = new FileInfo(appHostPath),
            Channel = stableChannel,
            ConfirmBinding = PromptBinding.CreateDefault<bool>(false),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null),
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => project.UpdatePackagesAsync(context, CancellationToken.None));

        var reloaded = AspireConfigFile.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        Assert.Equal(PackageChannelNames.Staging, reloaded.Channel);
        Assert.Equal("1.0.0", reloaded.SdkVersion);
        Assert.Equal("1.0.0", reloaded.Packages?["Aspire.Hosting"]);
    }

    [Fact]
    public async Task UpdatePackagesAsync_ExplicitStagingChannel_WhenRegenerationFails_DoesNotMutateConfig()
    {
        var configPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, """
            {
              "sdk": { "version": "1.0.0" },
              "packages": { "Aspire.Hosting": "1.0.0" }
            }
            """);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        var stagingCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, packageId, _, _, _, _, _) =>
                Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                [
                    new Aspire.Shared.NuGetPackageCli { Id = packageId, Version = "2.0.0", Source = "staging" }
                ])
        };

        var stagingChannel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Staging,
            PackageChannelQuality.Both,
            [new PackageMapping("Aspire*", "staging")],
            stagingCache,
            features: new TestFeatures(), NullLogger.Instance);

        var interactionService = new TestInteractionService
        {
            ConfirmCallback = (_, _) => true
        };

        var project = CreateGuestAppHostProject(interactionService: interactionService);

        var context = new UpdatePackagesContext
        {
            AppHostFile = new FileInfo(appHostPath),
            Channel = stagingChannel,
            ConfirmBinding = PromptBinding.CreateDefault<bool>(false),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null),
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => project.UpdatePackagesAsync(context, CancellationToken.None));

        var reloaded = AspireConfigFile.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.Channel);
        Assert.Equal("1.0.0", reloaded.SdkVersion);
        Assert.Equal("1.0.0", reloaded.Packages?["Aspire.Hosting"]);
    }

    [Fact]
    public async Task UpdatePackagesAsync_ExplicitStableChannel_DoesNotPersistStableChannelWhenProjectIsUpToDate()
    {
        var configPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, """
            {
              "sdk": { "version": "2.0.0" },
              "channel": "staging",
              "packages": { "Aspire.Hosting": "2.0.0" }
            }
            """);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        var stableCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, packageId, _, _, _, _, _) =>
                Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                [
                    new Aspire.Shared.NuGetPackageCli { Id = packageId, Version = "2.0.0", Source = "stable" }
                ])
        };

        var stableChannel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Stable,
            PackageChannelQuality.Both,
            [new PackageMapping("Aspire.*", "stable")],
            stableCache,
            features: new TestFeatures(), NullLogger.Instance);

        var project = CreateGuestAppHostProject();

        var context = new UpdatePackagesContext
        {
            AppHostFile = new FileInfo(appHostPath),
            Channel = stableChannel,
            ConfirmBinding = PromptBinding.CreateDefault<bool>(false),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null),
        };

        var result = await project.UpdatePackagesAsync(context, CancellationToken.None);

        Assert.False(result.UpdatesApplied);
        var reloaded = AspireConfigFile.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        Assert.Equal(PackageChannelNames.Staging, reloaded.Channel);
        Assert.Equal("2.0.0", reloaded.SdkVersion);
        Assert.Equal("2.0.0", reloaded.Packages?["Aspire.Hosting"]);
    }

    /// <summary>
    /// Regression test for the v3 channel refactor: <c>aspire run</c> must be a pure read
    /// for <c>aspire.config.json#channel</c>. A no-op rewrite (same value) or a silent
    /// identity-channel pin (when unset) on every invocation is not useful work and
    /// hides intent — the seed write at <c>aspire init</c> / scaffolding time and the
    /// explicit channel resolution in <c>aspire update</c> are the only legitimate
    /// channel-write paths.
    /// </summary>
    /// <remarks>
    /// The test seeds <c>aspire.config.json</c> with a known channel value, drives
    /// <see cref="GuestAppHostProject.RunAsync"/> past the channel-write site (via a
    /// fake <see cref="IAppHostServerProject"/> that returns a failed prepare result so
    /// <c>RunAsync</c> takes the early <c>FailedToBuildArtifacts</c> return), and then
    /// reloads <c>aspire.config.json</c> from disk to assert the on-disk channel is
    /// unchanged. The identity channel is set to a distinctive value
    /// (<c>pr-99999</c>) so any accidental identity pin would be detectable.
    /// </remarks>
    [Theory]
    [InlineData("stable")]
    [InlineData(null)]
    public async Task RunAsync_DoesNotMutateConfigChannel(string? seededChannel)
    {
        var configPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        var seededJson = seededChannel is null
            ? """
              {
                "sdk": { "version": "1.0.0" },
                "packages": { "Aspire.Hosting": "1.0.0" }
              }
              """
            : $$"""
              {
                "sdk": { "version": "1.0.0" },
                "channel": "{{seededChannel}}",
                "packages": { "Aspire.Hosting": "1.0.0" }
              }
              """;
        await File.WriteAllTextAsync(configPath, seededJson);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        // Drive RunAsync past the (now-removed) channel-write site by returning a fake
        // apphost server whose PrepareAsync reports failure. RunAsync takes the early
        // FailedToBuildArtifacts return without touching the network or starting a server.
        var factory = new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (path, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeFailingAppHostServerProject(path))
        };

        var project = CreateGuestAppHostProject(
            identityChannel: "pr-99999",
            appHostServerProjectFactory: factory);

        var context = new AppHostProjectContext
        {
            AppHostFile = new FileInfo(appHostPath),
            WorkingDirectory = _workspace.WorkspaceRoot,
        };

        var exitCode = await project.RunAsync(context, CancellationToken.None);
        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode);

        var reloaded = AspireConfigFile.Load(_workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        // Pre-fix, RunAsync would have written `seededChannel ?? "pr-99999"` here on every
        // invocation. Post-fix, RunAsync is a pure read for the channel.
        Assert.Equal(seededChannel, reloaded.Channel);
    }

    [Theory]
    [InlineData(CliExitCodes.Success, "The AppHost server process exited")]
    [InlineData(42, "The AppHost server process exited unexpectedly with exit code 42")]
    [InlineData(null, "The AppHost server process exited unexpectedly")]
    public async Task StartBackchannelConnectionAsync_WhenGuestServerExitsBeforeBackchannelConnects_ReportsExitCodeWhenKnown(
        int? serverExitCode,
        string expectedMessage)
    {
        var backchannel = new TestAppHostBackchannel
        {
            ConnectAsyncCallback = (_, _) => throw new SocketException((int)SocketError.ConnectionRefused)
        };
        var project = CreateGuestAppHostProject(backchannel: backchannel);
        var serverSession = new FakeAppHostServerSession
        {
            ServerHasExited = true,
            ServerExitCode = serverExitCode
        };
        var backchannelCompletionSource = new TaskCompletionSource<IAppHostCliBackchannel>(TaskCreationOptions.RunContinuationsAsynchronously);

        await InvokeStartBackchannelConnectionAsync(project, serverSession, backchannelCompletionSource);

        var exception = await Assert.ThrowsAsync<FailedToConnectBackchannelConnection>(
            () => backchannelCompletionSource.Task).DefaultTimeout();
        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task StartBackchannelConnectionAsync_UsesConfiguredBackchannelTimeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.CliBackchannelConnectTimeoutSeconds] = "0"
            })
            .Build();
        var backchannel = new TestAppHostBackchannel
        {
            ConnectAsyncCallback = (_, _) => throw new SocketException((int)SocketError.ConnectionRefused)
        };
        var project = CreateGuestAppHostProject(backchannel: backchannel, configuration: configuration);
        var serverSession = new FakeAppHostServerSession();
        var backchannelCompletionSource = new TaskCompletionSource<IAppHostCliBackchannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        try
        {
            await InvokeStartBackchannelConnectionAsync(
                project,
                serverSession,
                backchannelCompletionSource,
                cancellationSource.Token);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }

        Assert.True(backchannelCompletionSource.Task.IsFaulted, "The configured immediate timeout should fault the guest backchannel wait before test cancellation.");
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => backchannelCompletionSource.Task);
        Assert.Contains("after 0 seconds", exception.Message);
    }

    [Fact]
    public async Task RunAsync_PassesWorkloadIdToAppHostServerEnvironment()
    {
        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");
        var appHostFile = new FileInfo(appHostPath);
        var expectedWorkloadId = AppHostWorkloadId.Create(appHostFile);

        var projectFactory = new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (path, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeSucceedingAppHostServerProject(path))
        };

        var serverSession = new FakeAppHostServerSession
        {
            GetRpcClientAsyncCallback = _ => Task.FromException<IAppHostRpcClient>(
                new InvalidOperationException("Stop after the server launch environment has been captured."))
        };
        var sessionFactory = new FakeAppHostServerSessionFactory
        {
            Session = serverSession
        };
        var project = CreateGuestAppHostProject(
            appHostServerProjectFactory: projectFactory,
            serverSessionFactory: sessionFactory);

        var context = new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            WorkingDirectory = _workspace.WorkspaceRoot,
            EnvironmentVariables = new Dictionary<string, string>()
        };

        var exitCode = await project.RunAsync(context, CancellationToken.None);

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.True(serverSession.StartAsyncCalled);
        Assert.NotNull(sessionFactory.CapturedEnvironmentVariables);
        Assert.Equal(expectedWorkloadId, sessionFactory.CapturedEnvironmentVariables[KnownConfigNames.DcpWorkloadId]);
    }

    [Fact]
    public async Task PublishAsync_PassesResolvedAspireHomeToAppHostServerEnvironment()
    {
        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");
        var appHostFile = new FileInfo(appHostPath);

        var projectFactory = new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (path, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeSucceedingAppHostServerProject(path))
        };
        var serverSession = new FakeAppHostServerSession
        {
            GetRpcClientAsyncCallback = _ => Task.FromException<IAppHostRpcClient>(
                new InvalidOperationException("Stop after the server launch environment has been captured."))
        };
        var sessionFactory = new FakeAppHostServerSessionFactory
        {
            Session = serverSession
        };
        var project = CreateGuestAppHostProject(
            appHostServerProjectFactory: projectFactory,
            serverSessionFactory: sessionFactory);
        var context = new PublishContext
        {
            AppHostFile = appHostFile,
            WorkingDirectory = _workspace.WorkspaceRoot
        };

        var exitCode = await project.PublishAsync(context, CancellationToken.None);

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, exitCode);
        Assert.True(serverSession.StartAsyncCalled);
        Assert.NotNull(sessionFactory.CapturedEnvironmentVariables);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, ".home", ".aspire"),
            sessionFactory.CapturedEnvironmentVariables[KnownConfigNames.AspireHome]);
    }

    [Fact]
    public void IsUsingProjectReferencesReturnsFalseWhenIdentityIsOverridden()
    {
        // When ASPIRE_CLI_* identity overrides (or the install sidecar) are active the CLI is
        // emulating an installed build, which is never resolving Aspire packages through in-repo
        // project references. This must hold even for a source (DEBUG) build run from inside the
        // Aspire repo, where AspireRepositoryDetector would otherwise match the repo's Aspire.slnx
        // (via its Environment.ProcessPath fallback) and force project-reference mode — which
        // short-circuits channel resolution so an emulated staging/daily apphost silently resolves
        // stable nuget.org packages instead of its pinned channel's feed.
        var project = CreateGuestAppHostProject(identityOverridden: true);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");

        Assert.False(project.IsUsingProjectReferences(new FileInfo(appHostPath)));
    }

    private GuestAppHostProject CreateGuestAppHostProject()
        => CreateGuestAppHostProject(interactionService: null, identityChannel: "local");

    /// <summary>
    /// Regression test for https://github.com/microsoft/aspire/issues/18103:
    /// During <c>aspire update</c>, the code-generation step calls
    /// <c>WarnIfCliSdkVersionSkew</c> which reads the SDK version from disk. At that
    /// point the in-memory config has already been updated to the CLI's version, but
    /// the file hasn't been saved yet. The method should not emit a version-skew warning
    /// when the update is actively aligning versions.
    /// </summary>
    /// <remarks>
    /// The test drives <see cref="GuestAppHostProject.UpdatePackagesAsync"/> to demonstrate
    /// the update scenario (stale on-disk SDK version, update available to match CLI). With
    /// <see cref="FakeSucceedingAppHostServerProject"/> and <see cref="FakeAppHostServerSession"/>
    /// (which returns empty results from <c>GenerateCodeAsync</c>), the full update flow
    /// succeeds. The assertion validates that the skew-warning method does not emit a spurious
    /// warning for the stale on-disk version when the update is aligning versions to the CLI.
    /// </remarks>
    [Fact]
    public async Task UpdatePackagesAsync_DoesNotEmitStaleVersionSkewWarningDuringUpdate()
    {
        var cliVersion = VersionHelper.GetDefaultSdkVersion();
        var staleVersion = "1.0.0";

        var configPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "sdk": { "version": "{{staleVersion}}" },
              "packages": { "Aspire.Hosting": "{{staleVersion}}" }
            }
            """);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        // Return the CLI version as the latest available, so aspire update would align them.
        var fakeCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, packageId, _, _, _, _, _) =>
                Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                [
                    new Aspire.Shared.NuGetPackageCli { Id = packageId, Version = cliVersion, Source = "test" }
                ])
        };

        var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures(), NullLogger.Instance);

        var interactionService = new TestInteractionService
        {
            ConfirmCallback = (_, _) => true
        };

        var factory = new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (appPath, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeSucceedingAppHostServerProject(appPath))
        };

        IAppHostServerSessionFactory sessionFactory = new FakeAppHostServerSessionFactory();

        var project = CreateGuestAppHostProject(
            interactionService: interactionService,
            appHostServerProjectFactory: factory,
            serverSessionFactory: sessionFactory);

        var context = new UpdatePackagesContext
        {
            AppHostFile = new FileInfo(appHostPath),
            Channel = implicitChannel,
            ConfirmBinding = PromptBinding.CreateDefault<bool>(false),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null),
        };

        // UpdatePackagesAsync will go through BuildAndGenerateSdkAsync → GenerateCodeViaRpcAsync
        // which calls WarnIfCliSdkVersionSkew reading the stale on-disk config.
        // It should NOT warn because the update is aligning versions to match the CLI.
        await project.UpdatePackagesAsync(context, CancellationToken.None);

        Assert.Empty(interactionService.DisplayedErrors);
        Assert.Collection(interactionService.DisplayedMessages,
            m =>
            {
                Assert.Equal("package", m.Emoji.Name);
                Assert.Equal($"Aspire SDK {staleVersion} to {cliVersion}", Markup.Remove(m.Message));
            },
            m =>
            {
                Assert.Equal("package", m.Emoji.Name);
                Assert.Equal($"Aspire.Hosting {staleVersion} to {cliVersion}", Markup.Remove(m.Message));
            },
            m =>
            {
                Assert.Equal("warning", m.Emoji.Name);
                Assert.Equal(ErrorStrings.LegacyTypeScriptAppHostWarning, Markup.Remove(m.Message));
            },
            m =>
            {
                Assert.Equal("package", m.Emoji.Name);
                Assert.Equal(UpdateCommandStrings.RegeneratedSdkCode, m.Message);
            });
    }

    /// <summary>
    /// Verifies that <c>WarnIfCliSdkVersionSkew</c> emits the
    /// <see cref="ErrorStrings.CodegenVersionSkewWarning"/> when the on-disk SDK version
    /// genuinely differs from the CLI version and the update target does NOT align them.
    /// </summary>
    [Fact]
    public async Task UpdatePackagesAsync_EmitsVersionSkewWarningWhenTargetDiffersFromCli()
    {
        var staleVersion = "1.0.0";
        var updateTargetVersion = "2.0.0"; // Different from CLI version — legitimate skew

        var configPath = Path.Combine(_workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "sdk": { "version": "{{staleVersion}}" },
              "packages": { "Aspire.Hosting": "{{staleVersion}}" }
            }
            """);

        var appHostPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "// test apphost");

        // Return a version that does NOT match the CLI version — the skew is genuine.
        var fakeCache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, packageId, _, _, _, _, _) =>
                Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                [
                    new Aspire.Shared.NuGetPackageCli { Id = packageId, Version = updateTargetVersion, Source = "test" }
                ])
        };

        var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures(), NullLogger.Instance);

        var interactionService = new TestInteractionService
        {
            ConfirmCallback = (_, _) => true
        };

        var factory = new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (appPath, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeSucceedingAppHostServerProject(appPath))
        };

        IAppHostServerSessionFactory sessionFactory = new FakeAppHostServerSessionFactory();

        var project = CreateGuestAppHostProject(
            interactionService: interactionService,
            appHostServerProjectFactory: factory,
            serverSessionFactory: sessionFactory);

        var context = new UpdatePackagesContext
        {
            AppHostFile = new FileInfo(appHostPath),
            Channel = implicitChannel,
            ConfirmBinding = PromptBinding.CreateDefault<bool>(false),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null),
        };

        await project.UpdatePackagesAsync(context, CancellationToken.None);

        var cliVersion = VersionHelper.GetDefaultSdkVersion();
        var expectedWarning = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            ErrorStrings.CodegenVersionSkewWarning,
            cliVersion,
            staleVersion);

        Assert.Empty(interactionService.DisplayedErrors);
        Assert.Collection(interactionService.DisplayedMessages,
            m =>
            {
                Assert.Equal("package", m.Emoji.Name);
                Assert.Equal($"Aspire SDK {staleVersion} to {updateTargetVersion}", Markup.Remove(m.Message));
            },
            m =>
            {
                Assert.Equal("package", m.Emoji.Name);
                Assert.Equal($"Aspire.Hosting {staleVersion} to {updateTargetVersion}", Markup.Remove(m.Message));
            },
            m =>
            {
                Assert.Equal("warning", m.Emoji.Name);
                Assert.Contains(expectedWarning, m.Message);
            },
            m =>
            {
                Assert.Equal("warning", m.Emoji.Name);
                Assert.Equal(ErrorStrings.LegacyTypeScriptAppHostWarning, Markup.Remove(m.Message));
            },
            m =>
            {
                Assert.Equal("package", m.Emoji.Name);
                Assert.Equal(UpdateCommandStrings.RegeneratedSdkCode, m.Message);
            });
    }

    private string CreateMatchingSocketFile(string appHostPath, int pid)
    {
        var backchannelsDir = Path.Combine(_workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var resolvedAppHostPath = PathNormalizer.ResolveSymlinks(appHostPath);
        var prefix = BackchannelConstants.ComputeSocketPrefix(resolvedAppHostPath, _workspace.WorkspaceRoot.FullName);
        var appHostId = Path.GetFileName(prefix);
        var socketPath = Path.Combine(
            backchannelsDir,
            $"{appHostId}a1b2C3d4.{pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        File.WriteAllText(socketPath, "");
        return socketPath;
    }

    private GuestAppHostProject CreateGuestAppHostProject(
        TestInteractionService? interactionService = null,
        string identityChannel = "local",
        TestAppHostBackchannel? backchannel = null,
        TestAppHostServerProjectFactory? appHostServerProjectFactory = null,
        IAppHostServerSessionFactory? serverSessionFactory = null,
        bool identityOverridden = false,
        string languageId = "typescript/nodejs",
        IEnvironment? environment = null,
        DirectoryInfo? homeDirectory = null,
        IConfiguration? configuration = null)
    {
        var effectiveConfiguration = configuration ?? _configuration;

        var language = new LanguageInfo(
            LanguageId: languageId,
            DisplayName: "TypeScript (Node.js)",
            PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
            DetectionPatterns: ["apphost.ts"],
            CodeGenerator: "TypeScript");

        var logFilePath = Path.Combine(_workspace.WorkspaceRoot.FullName, $"test-guest-{Guid.NewGuid()}.log");

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(AppContext.BaseDirectory),
            identityChannel: identityChannel,
            logFilePath: logFilePath,
            identityOverridden: identityOverridden,
            homeDirectory: homeDirectory);

        // Construct a real graceful-shutdown window so the contract matches production:
        // GuestAppHostProject requires it even when a test exits the Run path early
        // (e.g. via FailedToBuildArtifacts) without exercising shutdown. The test fake stands in for
        // ConsoleCancellationManager so the fixture doesn't register process-global OS signal handlers;
        // none of the tests here drive the launcher or AppHostServerSession paths that would fire it.
        var shutdownWindow = new TestGracefulShutdownWindow();

        return new GuestAppHostProject(
            language: language,
            interactionService: interactionService ?? new TestInteractionService(),
            backchannel: backchannel ?? new TestAppHostBackchannel(),
            appHostServerProjectFactory: appHostServerProjectFactory ?? new TestAppHostServerProjectFactory(),
            certificateService: new TestCertificateService(),
            runner: new TestDotNetCliRunner(),
            packagingService: new TestPackagingService(),
            configuration: effectiveConfiguration,
            features: new Features(effectiveConfiguration, NullLogger<Features>.Instance),
            languageDiscovery: new TestLanguageDiscovery(),
            executionContext: executionContext,
            environment: environment ?? new TestEnvironment(),
            logger: NullLogger<GuestAppHostProject>.Instance,
            fileLoggerProvider: new FileLoggerProvider(logFilePath, new TestStartupErrorWriter()),
            profilingTelemetry: _profilingTelemetry,
            gracefulShutdownSignaler: new NoOpGracefulSignaler(),
            shutdownService: shutdownWindow,
            serverSessionFactory: serverSessionFactory ?? new FakeAppHostServerSessionFactory(),
            timeProvider: TimeProvider.System);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ConfigureCertificateBundleEnvironmentAsync_SetsEnvironmentVariable_WhenExistingValueIsNotUsable(string? existingValue)
    {
        var project = CreateGuestAppHostProject();
        var envVars = new Dictionary<string, string>();
        if (existingValue is not null)
        {
            envVars["NODE_EXTRA_CA_CERTS"] = existingValue;
        }

        await project.ConfigureCertificateBundleEnvironmentAsync(
            envVars,
            _workspace.WorkspaceRoot,
            "/path/to/cert.pem",
            "NODE_EXTRA_CA_CERTS",
            "typescript-nodejs",
            TestContext.Current.CancellationToken);

        Assert.Equal("/path/to/cert.pem", envVars["NODE_EXTRA_CA_CERTS"]);
    }

    [Fact]
    public async Task ConfigureCertificateBundleEnvironmentAsync_ReusesDevCertificate_WhenAlreadyConfigured()
    {
        var devCertificatePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "aspire-dev-cert.pem");
        var project = CreateGuestAppHostProject();
        var envVars = new Dictionary<string, string>
        {
            ["NODE_EXTRA_CA_CERTS"] = Path.GetFileName(devCertificatePath)
        };

        await project.ConfigureCertificateBundleEnvironmentAsync(
            envVars,
            _workspace.WorkspaceRoot,
            devCertificatePath,
            "NODE_EXTRA_CA_CERTS",
            "typescript-nodejs",
            TestContext.Current.CancellationToken);

        Assert.Equal(devCertificatePath, envVars["NODE_EXTRA_CA_CERTS"]);
        Assert.False(Directory.Exists(Path.Combine(_workspace.WorkspaceRoot.FullName, "bundles")));
    }

    [Fact]
    public async Task ConfigureCertificateBundleEnvironmentAsync_UsesCaseInsensitivePathComparisonOnWindows()
    {
        var devCertificatePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "aspire-dev-cert.pem");
        var project = CreateGuestAppHostProject(environment: TestEnvironment.CreateWindows());
        var envVars = new Dictionary<string, string>
        {
            ["NODE_EXTRA_CA_CERTS"] = devCertificatePath.ToUpperInvariant()
        };

        await project.ConfigureCertificateBundleEnvironmentAsync(
            envVars,
            _workspace.WorkspaceRoot,
            devCertificatePath,
            "NODE_EXTRA_CA_CERTS",
            "typescript-nodejs",
            TestContext.Current.CancellationToken);

        Assert.Equal(devCertificatePath, envVars["NODE_EXTRA_CA_CERTS"]);
    }

    [Fact]
    public async Task ConfigureCertificateBundleEnvironmentAsync_DoesNotAssumeMacOSPathsAreCaseInsensitive()
    {
        var lowerCaseDirectory = Path.Combine(_workspace.WorkspaceRoot.FullName, "certificates");
        var upperCaseDirectory = Path.Combine(_workspace.WorkspaceRoot.FullName, "CERTIFICATES");
        Directory.CreateDirectory(lowerCaseDirectory);
        Directory.CreateDirectory(upperCaseDirectory);
        var devCertificatePath = Path.Combine(lowerCaseDirectory, "aspire.pem");
        var existingBundlePath = Path.Combine(upperCaseDirectory, "ASPIRE.PEM");
        await File.WriteAllTextAsync(existingBundlePath, "existing certificate", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(devCertificatePath, "development certificate", TestContext.Current.CancellationToken);
        var expectedDevCertificateContents = await File.ReadAllBytesAsync(devCertificatePath, TestContext.Current.CancellationToken);
        var expectedExistingBundleContents = await File.ReadAllBytesAsync(existingBundlePath, TestContext.Current.CancellationToken);
        byte[] expectedBundleContents = [.. expectedDevCertificateContents, (byte)'\n', .. expectedExistingBundleContents];
        var project = CreateGuestAppHostProject(environment: TestEnvironment.CreateMacOS());
        var envVars = new Dictionary<string, string>
        {
            ["NODE_EXTRA_CA_CERTS"] = existingBundlePath
        };

        await project.ConfigureCertificateBundleEnvironmentAsync(
            envVars,
            _workspace.WorkspaceRoot,
            devCertificatePath,
            "NODE_EXTRA_CA_CERTS",
            "typescript-nodejs",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(devCertificatePath, envVars["NODE_EXTRA_CA_CERTS"]);
        Assert.Equal(
            expectedBundleContents,
            await File.ReadAllBytesAsync(envVars["NODE_EXTRA_CA_CERTS"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfigureCertificateBundleEnvironmentAsync_DoesNotSet_WhenPemPathIsNull()
    {
        var project = CreateGuestAppHostProject();
        var envVars = new Dictionary<string, string>();

        await project.ConfigureCertificateBundleEnvironmentAsync(
            envVars,
            _workspace.WorkspaceRoot,
            devCertPemPath: null,
            environmentVariableName: "NODE_EXTRA_CA_CERTS",
            cacheFilePrefix: "typescript-nodejs",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(envVars.ContainsKey("NODE_EXTRA_CA_CERTS"));
    }

    [Fact]
    public async Task ConfigureCertificateBundleEnvironmentAsync_CreatesAndReusesCombinedBundle_WhenAlreadySet()
    {
        const string certificateBundleEnvironmentVariable = "NODE_EXTRA_CA_CERTS";
        const string configuredNodeExtraCaCertsKey = "Node_Extra_Ca_Certs";
        var homeDirectory = _workspace.CreateDirectory("bundle-home");
        var existingBundlePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "existing-ca-certs.pem");
        var inheritedBundlePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "inherited-ca-certs.pem");
        var devCertificateDirectory = Path.Combine(homeDirectory.FullName, ".aspire", "dev-certs");
        var devCertificatePath = Path.Combine(devCertificateDirectory, "aspire-dev-cert.pem");
        const string existingBundleContents = "-----BEGIN CERTIFICATE-----\nexisting\n-----END CERTIFICATE-----\n";
        const string inheritedBundleContents = "-----BEGIN CERTIFICATE-----\ninherited\n-----END CERTIFICATE-----\n";
        const string devCertificateContents = "-----BEGIN CERTIFICATE-----\ndev\n-----END CERTIFICATE-----";
        Directory.CreateDirectory(devCertificateDirectory);
        await File.WriteAllTextAsync(existingBundlePath, existingBundleContents, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(inheritedBundlePath, inheritedBundleContents, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(devCertificatePath, devCertificateContents, TestContext.Current.CancellationToken);

        var environment = TestEnvironment.CreateWindows(new Dictionary<string, string?>
        {
            [certificateBundleEnvironmentVariable] = inheritedBundlePath
        });
        var project = CreateGuestAppHostProject(environment: environment, homeDirectory: homeDirectory);
        var envVars = new Dictionary<string, string>
        {
            [certificateBundleEnvironmentVariable] = inheritedBundlePath,
            [configuredNodeExtraCaCertsKey] = Path.GetFileName(existingBundlePath)
        };
        var expectedBundleContents = Encoding.UTF8.GetBytes($"{devCertificateContents}\n{existingBundleContents}");
        var expectedHash = Convert.ToHexString(XxHash128.Hash(expectedBundleContents)).ToLowerInvariant();
        var expectedBundlePath = Path.Combine(
            homeDirectory.FullName,
            ".aspire",
            "dev-certs",
            "bundles",
            $"typescript-nodejs-{expectedHash}.pem");

        await project.ConfigureCertificateBundleEnvironmentAsync(
            envVars,
            _workspace.WorkspaceRoot,
            devCertificatePath,
            certificateBundleEnvironmentVariable,
            "typescript-nodejs",
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedBundlePath, envVars[certificateBundleEnvironmentVariable]);
        Assert.DoesNotContain(configuredNodeExtraCaCertsKey, envVars.Keys);
        Assert.Equal(expectedBundleContents, await File.ReadAllBytesAsync(expectedBundlePath, TestContext.Current.CancellationToken));

        var cachedWriteTime = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(expectedBundlePath, cachedWriteTime);
        cachedWriteTime = File.GetLastWriteTimeUtc(expectedBundlePath);
        envVars.Remove(certificateBundleEnvironmentVariable);
        envVars[configuredNodeExtraCaCertsKey] = Path.GetFileName(existingBundlePath);
        await project.ConfigureCertificateBundleEnvironmentAsync(
            envVars,
            _workspace.WorkspaceRoot,
            devCertificatePath,
            certificateBundleEnvironmentVariable,
            "typescript-nodejs",
            TestContext.Current.CancellationToken);

        Assert.Equal(cachedWriteTime, File.GetLastWriteTimeUtc(expectedBundlePath));
        Assert.DoesNotContain(configuredNodeExtraCaCertsKey, envVars.Keys);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(expectedBundlePath)!));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(expectedBundlePath));
        }
    }

    [Fact]
    public async Task ConfigureCertificateBundleEnvironmentAsync_PreservesExistingBundle_WhenCombinationFails()
    {
        var interactionService = new TestInteractionService();
        var project = CreateGuestAppHostProject(interactionService: interactionService);
        var devCertificatePath = Path.Combine(_workspace.WorkspaceRoot.FullName, "aspire-dev-cert.pem");
        await File.WriteAllTextAsync(devCertificatePath, "dev certificate", TestContext.Current.CancellationToken);
        var envVars = new Dictionary<string, string>
        {
            ["NODE_EXTRA_CA_CERTS"] = "missing-ca-certs.pem"
        };

        await project.ConfigureCertificateBundleEnvironmentAsync(
            envVars,
            _workspace.WorkspaceRoot,
            devCertificatePath,
            "NODE_EXTRA_CA_CERTS",
            "typescript-nodejs",
            TestContext.Current.CancellationToken);

        Assert.Equal("missing-ca-certs.pem", envVars["NODE_EXTRA_CA_CERTS"]);
        Assert.Single(interactionService.DisplayedMessages);
        Assert.Contains("existing certificate bundle will be used unchanged", interactionService.DisplayedMessages[0].Message);
    }

    private static async Task InvokeStartBackchannelConnectionAsync(
        GuestAppHostProject project,
        IAppHostServerSession serverSession,
        TaskCompletionSource<IAppHostCliBackchannel> backchannelCompletionSource,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(GuestAppHostProject).GetMethod(
            "StartBackchannelConnectionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(project, [
            serverSession,
            "fake.sock",
            backchannelCompletionSource,
            false,
            default(System.Diagnostics.ActivityContext),
            cancellationToken
        ]));
        await task.DefaultTimeout();
    }

    private sealed class NoOpGracefulSignaler : IProcessTreeGracefulShutdownSignaler
    {
        public Task<bool> RequestProcessTreeGracefulShutdownAsync(int pid, DateTimeOffset? startTime, bool includeStartTimeForDcp, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
