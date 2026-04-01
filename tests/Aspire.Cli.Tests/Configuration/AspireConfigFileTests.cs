// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Configuration;

public class AspireConfigFileTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Load_ReturnsNull_WhenFileDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.Null(result);
    }

    [Fact]
    public void Load_ReturnsConfig_WhenFileIsValid()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(configPath, """
            {
              "appHost": { "path": "MyApp/MyApp.csproj" },
              "channel": "daily"
            }
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Equal("MyApp/MyApp.csproj", result.AppHost?.Path);
        Assert.Equal("daily", result.Channel);
    }

    [Fact]
    public void Load_ReturnsConfig_WhenFileContainsJsonComments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(configPath, """
            {
              // This is a comment
              "appHost": {
                "path": "MyApp/MyApp.csproj" // inline comment
              },
              "channel": "stable"
            }
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Equal("MyApp/MyApp.csproj", result.AppHost?.Path);
        Assert.Equal("stable", result.Channel);
    }

    [Fact]
    public void Load_ReturnsConfig_WhenFileContainsTrailingCommas()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(configPath, """
            {
              "appHost": { "path": "MyApp/MyApp.csproj", },
              "channel": "daily",
            }
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Equal("MyApp/MyApp.csproj", result.AppHost?.Path);
    }

    [Fact]
    public void Load_ThrowsJsonException_WhenFileContainsInvalidJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(configPath, "{ invalid json content }");

        var ex = Assert.Throws<JsonException>(() => AspireConfigFile.Load(workspace.WorkspaceRoot.FullName));

        Assert.Contains(configPath, ex.Message);
        Assert.Contains("invalid JSON", ex.Message);
    }

    [Fact]
    public void Load_ThrowsJsonException_WithFilePath_WhenJsonIsTruncated()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(configPath, """{ "appHost": { "path": """);

        var ex = Assert.Throws<JsonException>(() => AspireConfigFile.Load(workspace.WorkspaceRoot.FullName));

        Assert.Contains(configPath, ex.Message);
    }

    [Fact]
    public void Load_ReturnsEmptyConfig_WhenFileIsEmptyObject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(configPath, "{}");

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Null(result.AppHost);
        Assert.Null(result.Channel);
    }

    [Fact]
    public void Save_CreatesFileWithExpectedContent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = new AspireConfigFile
        {
            AppHost = new AspireConfigAppHost { Path = "src/AppHost/AppHost.csproj" },
            Channel = "daily"
        };

        config.Save(workspace.WorkspaceRoot.FullName);

        var filePath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        Assert.True(File.Exists(filePath));

        var content = File.ReadAllText(filePath);
        Assert.Contains("src/AppHost/AppHost.csproj", content);
        Assert.Contains("daily", content);
    }

    [Fact]
    public void Save_CreatesDirectoryIfNeeded()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var subDir = Path.Combine(workspace.WorkspaceRoot.FullName, "nested", "dir");
        var config = new AspireConfigFile();

        config.Save(subDir);

        Assert.True(File.Exists(Path.Combine(subDir, AspireConfigFile.FileName)));
    }

    [Fact]
    public void Exists_ReturnsFalse_WhenFileDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.False(AspireConfigFile.Exists(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void Exists_ReturnsTrue_WhenFileExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), "{}");

        Assert.True(AspireConfigFile.Exists(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void SdkVersion_ReadsFromSdkObject()
    {
        var config = new AspireConfigFile
        {
            Sdk = new AspireConfigSdk { Version = "13.2.0" }
        };

        Assert.Equal("13.2.0", config.SdkVersion);
    }

    [Fact]
    public void SdkVersion_SetsOnSdkObject()
    {
        var config = new AspireConfigFile();

        config.SdkVersion = "13.2.0";

        Assert.NotNull(config.Sdk);
        Assert.Equal("13.2.0", config.Sdk.Version);
    }

    [Fact]
    public void SdkVersion_ReturnsNull_WhenSdkIsNull()
    {
        var config = new AspireConfigFile();

        Assert.Null(config.SdkVersion);
    }

    [Fact]
    public void GetEffectiveSdkVersion_ReturnsConfigValue_WhenSet()
    {
        var config = new AspireConfigFile
        {
            Sdk = new AspireConfigSdk { Version = "13.2.0" }
        };

        Assert.Equal("13.2.0", config.GetEffectiveSdkVersion("13.1.0"));
    }

    [Fact]
    public void GetEffectiveSdkVersion_ReturnsFallback_WhenNotSet()
    {
        var config = new AspireConfigFile();

        Assert.Equal("13.1.0", config.GetEffectiveSdkVersion("13.1.0"));
    }

    [Fact]
    public void AddOrUpdatePackage_AddsNewPackage()
    {
        var config = new AspireConfigFile();

        config.AddOrUpdatePackage("Aspire.Hosting.Redis", "13.2.0");

        Assert.NotNull(config.Packages);
        Assert.Equal("13.2.0", config.Packages["Aspire.Hosting.Redis"]);
    }

    [Fact]
    public void AddOrUpdatePackage_UpdatesExistingPackage()
    {
        var config = new AspireConfigFile
        {
            Packages = new Dictionary<string, string> { ["Aspire.Hosting.Redis"] = "13.1.0" }
        };

        config.AddOrUpdatePackage("Aspire.Hosting.Redis", "13.2.0");

        Assert.Equal("13.2.0", config.Packages["Aspire.Hosting.Redis"]);
    }

    [Fact]
    public void RemovePackage_RemovesExistingPackage()
    {
        var config = new AspireConfigFile
        {
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "13.2.0",
                ["Aspire.Hosting.PostgreSQL"] = "13.2.0"
            }
        };

        var removed = config.RemovePackage("Aspire.Hosting.Redis");

        Assert.True(removed);
        Assert.DoesNotContain("Aspire.Hosting.Redis", config.Packages.Keys);
        Assert.Contains("Aspire.Hosting.PostgreSQL", config.Packages.Keys);
    }

    [Fact]
    public void RemovePackage_ReturnsFalse_WhenPackageDoesNotExist()
    {
        var config = new AspireConfigFile();

        var removed = config.RemovePackage("Aspire.Hosting.Redis");

        Assert.False(removed);
    }

    [Fact]
    public void GetIntegrationReferences_ReturnsBasePackage_WhenNoPackages()
    {
        var config = new AspireConfigFile();

        var refs = config.GetIntegrationReferences("13.2.0", "/tmp").ToList();

        Assert.Single(refs);
        Assert.Equal("Aspire.Hosting", refs[0].Name);
    }

    [Fact]
    public void GetIntegrationReferences_IncludesPackagesAndBasePackage()
    {
        var config = new AspireConfigFile
        {
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "13.2.0"
            }
        };

        var refs = config.GetIntegrationReferences("13.2.0", "/tmp").ToList();

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting");
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting.Redis");
    }

    [Fact]
    public void Load_ReturnsConfig_WhenFeaturesAreBooleans()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(configPath, """
            {
              "features": { "polyglotSupportEnabled": true, "showAllTemplates": false }
            }
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.NotNull(result.Features);
        Assert.True(result.Features["polyglotSupportEnabled"]);
        Assert.False(result.Features["showAllTemplates"]);
    }

    [Fact]
    public void Load_ReturnsConfig_WhenFeaturesAreStringBooleans()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Simulates what happens when ConfigurationService.SetNestedValue wrote "true"/"false" as strings
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(configPath, """
            {
              "features": { "polyglotSupportEnabled": "true", "showAllTemplates": "false" }
            }
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.NotNull(result.Features);
        Assert.True(result.Features["polyglotSupportEnabled"]);
        Assert.False(result.Features["showAllTemplates"]);
    }

    [Fact]
    public void Save_Load_RoundTrips_WithFeatures()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = new AspireConfigFile
        {
            Features = new Dictionary<string, bool>
            {
                ["polyglotSupportEnabled"] = true,
                ["showAllTemplates"] = false
            }
        };

        config.Save(workspace.WorkspaceRoot.FullName);
        var loaded = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(loaded?.Features);
        Assert.True(loaded.Features["polyglotSupportEnabled"]);
        Assert.False(loaded.Features["showAllTemplates"]);
    }

    [Fact]
    public void Load_RoundTrips_WithProfiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = new AspireConfigFile
        {
            AppHost = new AspireConfigAppHost { Path = "App.csproj" },
            Profiles = new Dictionary<string, AspireConfigProfile>
            {
                ["default"] = new AspireConfigProfile
                {
                    ApplicationUrl = "https://localhost:5001",
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["ASPNETCORE_ENVIRONMENT"] = "Development"
                    }
                }
            }
        };

        config.Save(workspace.WorkspaceRoot.FullName);
        var loaded = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(loaded);
        Assert.Equal("App.csproj", loaded.AppHost?.Path);
        Assert.NotNull(loaded.Profiles);
        Assert.True(loaded.Profiles.ContainsKey("default"));
        Assert.Equal("https://localhost:5001", loaded.Profiles["default"].ApplicationUrl);
    }
}
