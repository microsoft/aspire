// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Aspire.Cli.Tests.Projects;

public class GuestAppHostProjectTests : IDisposable
{
    private const string AspNetCoreEnvironmentVariableName = "ASPNETCORE_ENVIRONMENT";

    private readonly TemporaryWorkspace _workspace;
    private readonly IConfiguration _configuration;
    private readonly ProfilingTelemetry _profilingTelemetry;

    public GuestAppHostProjectTests(ITestOutputHelper outputHelper)
    {
        _workspace = TemporaryWorkspace.Create(outputHelper);
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

        Assert.Equal("https://localhost:16319;http://localhost:16320", envVars["ASPNETCORE_URLS"]);
        Assert.Equal("Development", envVars["ASPNETCORE_ENVIRONMENT"]);
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

        Assert.Equal("Production", envVars["DOTNET_ENVIRONMENT"]);
        Assert.False(envVars.ContainsKey("ASPNETCORE_ENVIRONMENT"));
    }

    [Fact]
    public void GetServerEnvironmentVariables_IgnoresLaunchProfileEnvironmentVariablesWhenRequested()
    {
        var envVars = GuestAppHostProject.GetServerEnvironmentVariables(
            launchProfileEnvironmentVariables: new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = "https://localhost:16319;http://localhost:16320",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "https://localhost:17269",
                ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "https://localhost:18269"
            },
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            includeLaunchProfileEnvironmentVariables: false,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", envVars["DOTNET_ENVIRONMENT"]);
        Assert.False(envVars.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal("https://localhost:16319;http://localhost:16320", envVars["ASPNETCORE_URLS"]);
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
                ["ASPNETCORE_URLS"] = "https://localhost:16319;http://localhost:16320",
                ["ASPIRE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Development",
            },
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inheritedEnvironmentVariables: new Dictionary<string, string?>(),
            args: ["--environment", "Staging"]);

        Assert.Equal("Staging", envVars["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Development", envVars["ASPNETCORE_ENVIRONMENT"]);
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
                ["ASPNETCORE_URLS"] = "http://context"
            },
            new Dictionary<string, string>
            {
                ["SSL_CERT_DIR"] = "/tmp/certs"
            });

        Assert.Equal("context", envVars["CUSTOM_CONTEXT_VARIABLE"]);
        Assert.Equal("https://localhost:16319;http://localhost:16320", envVars["ASPNETCORE_URLS"]);
        Assert.Equal("Staging", envVars["ASPIRE_ENVIRONMENT"]);
        Assert.Equal("Staging", envVars["DOTNET_ENVIRONMENT"]);
        Assert.False(envVars.ContainsKey("ASPNETCORE_ENVIRONMENT"));
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
                ["ASPNETCORE_URLS"] = "https://localhost:16319;http://localhost:16320",
                ["ASPIRE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "https://localhost:17269",
                ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "https://localhost:18269"
            },
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            includeLaunchProfileEnvironmentVariables: false,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", envVars["DOTNET_ENVIRONMENT"]);
        Assert.False(envVars.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal("https://localhost:16319;http://localhost:16320", envVars["ASPNETCORE_URLS"]);
        Assert.Equal("https://localhost:17269", envVars["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.Equal("https://localhost:18269", envVars["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]);
        Assert.False(envVars.ContainsKey("ASPIRE_ENVIRONMENT"));
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_EnvironmentArgumentTakesPrecedenceOverLaunchProfileEnvironmentVariables()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>(),
            launchProfileEnvironmentVariables: new Dictionary<string, string>
            {
                ["ASPIRE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Development",
            },
            defaultEnvironment: AppHostEnvironmentDefaults.ProductionEnvironmentName,
            inheritedEnvironmentVariables: new Dictionary<string, string?>(),
            args: ["--environment", "Staging"]);

        Assert.Equal("Staging", envVars["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Development", envVars["ASPNETCORE_ENVIRONMENT"]);
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

        Assert.Equal("Staging", envVars["DOTNET_ENVIRONMENT"]);
        Assert.False(envVars.ContainsKey("ASPNETCORE_ENVIRONMENT"));
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_DotnetEnvironmentTakesPrecedenceOverAspireEnvironment()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>
            {
                [AppHostEnvironmentDefaults.DotNetEnvironmentVariableName] = "Production",
                [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Staging"
            },
            launchProfileEnvironmentVariables: null,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Production", envVars["DOTNET_ENVIRONMENT"]);
        Assert.False(envVars.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal("Staging", envVars["ASPIRE_ENVIRONMENT"]);
    }

    [Fact]
    public void CreateGuestEnvironmentVariables_AspireEnvironmentTakesPrecedenceOverAspNetCoreEnvironment()
    {
        var envVars = GuestAppHostProject.CreateGuestEnvironmentVariables(
            contextEnvironmentVariables: new Dictionary<string, string>
            {
                [AppHostEnvironmentDefaults.AspireEnvironmentVariableName] = "Testing",
                [AspNetCoreEnvironmentVariableName] = "Staging"
            },
            launchProfileEnvironmentVariables: null,
            inheritedEnvironmentVariables: new Dictionary<string, string?>());

        Assert.Equal("Testing", envVars["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Staging", envVars["ASPNETCORE_ENVIRONMENT"]);
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

        var sessionFactory = new TestAppHostServerSessionFactory();

        var project = CreateGuestAppHostProject(
            interactionService: interactionService,
            appHostServerProjectFactory: factory,
            appHostServerSessionFactory: sessionFactory);

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

        var sessionFactory = new TestAppHostServerSessionFactory();

        var project = CreateGuestAppHostProject(
            interactionService: interactionService,
            appHostServerProjectFactory: factory,
            appHostServerSessionFactory: sessionFactory);

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
                Assert.Equal("package", m.Emoji.Name);
                Assert.Equal(UpdateCommandStrings.RegeneratedSdkCode, m.Message);
            });
    }

    private string CreateMatchingSocketFile(string appHostPath, int pid)
    {
        var backchannelsDir = Path.Combine(_workspace.WorkspaceRoot.FullName, ".aspire", "cli", "bch");
        Directory.CreateDirectory(backchannelsDir);

        var resolvedAppHostPath = PathNormalizer.ResolveSymlinks(appHostPath);
        var prefix = AppHostHelper.ComputeAuxiliarySocketPrefix(resolvedAppHostPath, _workspace.WorkspaceRoot.FullName);
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
        TestAppHostServerProjectFactory? appHostServerProjectFactory = null,
        IAppHostServerSessionFactory? appHostServerSessionFactory = null,
        bool identityOverridden = false)
    {
        var language = new LanguageInfo(
            LanguageId: "typescript/nodejs",
            DisplayName: "TypeScript (Node.js)",
            PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
            DetectionPatterns: ["apphost.ts"],
            CodeGenerator: "TypeScript");

        var logFilePath = Path.Combine(_workspace.WorkspaceRoot.FullName, $"test-guest-{Guid.NewGuid()}.log");

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(AppContext.BaseDirectory),
            identityChannel: identityChannel,
            logFilePath: logFilePath,
            identityOverridden: identityOverridden);

        return new GuestAppHostProject(
            language: language,
            interactionService: interactionService ?? new TestInteractionService(),
            backchannel: new TestAppHostBackchannel(),
            appHostServerProjectFactory: appHostServerProjectFactory ?? new TestAppHostServerProjectFactory(),
            appHostServerSessionFactory: appHostServerSessionFactory ?? new TestAppHostServerSessionFactory(),
            certificateService: new TestCertificateService(),
            runner: new TestDotNetCliRunner(),
            packagingService: new TestPackagingService(),
            configuration: _configuration,
            features: new Features(_configuration, NullLogger<Features>.Instance),
            languageDiscovery: new TestLanguageDiscovery(),
            executionContext: executionContext,
            logger: NullLogger<GuestAppHostProject>.Instance,
            fileLoggerProvider: new FileLoggerProvider(logFilePath, new TestStartupErrorWriter()),
            profilingTelemetry: _profilingTelemetry,
            timeProvider: TimeProvider.System);
    }

}
