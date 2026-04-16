// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class GuestAppHostProjectTests(ITestOutputHelper outputHelper) : IDisposable
{
    private const string AspNetCoreEnvironmentVariableName = "ASPNETCORE_ENVIRONMENT";

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(outputHelper);

    public void Dispose()
    {
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
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting" && r.Version == "13.1.0" && r.Source != IntegrationSource.Project);
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting.Redis" && r.Version == "13.1.0" && r.Source != IntegrationSource.Project);
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
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting" && r.Source == IntegrationSource.Nuget);
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting.Redis" && r.Source == IntegrationSource.Nuget);
        var projectRef = Assert.Single(refs, r => r.Source == IntegrationSource.Project);
        Assert.Equal("Aspire.Hosting.MyCustom", projectRef.Name);
        Assert.Null(projectRef.Version);
        Assert.NotNull(projectRef.Path);
        Assert.EndsWith(".csproj", projectRef.Path);
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

    private static GuestAppHostProject CreateGuestAppHostProject()
    {
        var language = new LanguageInfo(
            LanguageId: "typescript/nodejs",
            DisplayName: "TypeScript (Node.js)",
            PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
            DetectionPatterns: ["apphost.ts"],
            CodeGenerator: "TypeScript");

        var configuration = new ConfigurationBuilder().Build();

        var logFilePath = Path.Combine(Path.GetTempPath(), $"test-guest-{Guid.NewGuid()}.log");

        return new GuestAppHostProject(
            language: language,
            interactionService: new TestInteractionService(),
            backchannel: new TestAppHostBackchannel(),
            appHostServerProjectFactory: new TestAppHostServerProjectFactory(),
            certificateService: new TestCertificateService(),
            runner: new TestDotNetCliRunner(),
            packagingService: new TestPackagingService(),
            configuration: configuration,
            features: new Features(configuration, NullLogger<Features>.Instance),
            languageDiscovery: new TestLanguageDiscovery(),
            logger: NullLogger<GuestAppHostProject>.Instance,
            fileLoggerProvider: new FileLoggerProvider(logFilePath, new TestStartupErrorWriter()));
    }
}
