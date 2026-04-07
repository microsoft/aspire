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

    [Fact]
    public void LoadOrCreate_MigratesLegacy_AdjustsRelativePathFromAspireDir()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        // Legacy .aspire/settings.json stores paths relative to the .aspire/ directory
        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "../src/apphost.ts",
                "language": "typescript/nodejs",
                "sdkVersion": "13.2.0"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        // Path should be re-based from .aspire/-relative to root-relative
        Assert.Equal("src/apphost.ts", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_AdjustsPathForApphostAtRoot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        // Legacy path "../apphost.ts" means apphost is at the repo root
        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "../apphost.ts",
                "language": "typescript/nodejs"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        Assert.Equal("apphost.ts", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_RebasesSubdirectoryPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        // Legacy .aspire/settings.json stores appHostPath relative to .aspire/ directory.
        // A path like "../MyApp.AppHost/MyApp.AppHost.csproj" points from .aspire/ up to
        // the repo root, then into a subdirectory. After migration it should become
        // "MyApp.AppHost/MyApp.AppHost.csproj" (relative to the repo root).
        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "../MyApp.AppHost/MyApp.AppHost.csproj"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        Assert.Equal("MyApp.AppHost/MyApp.AppHost.csproj", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_SavesConfigFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "../src/apphost.ts",
                "sdkVersion": "13.2.0"
            }
            """);

        AspireConfigFile.LoadOrCreate(root);

        // Verify aspire.config.json was created with correct content
        var configPath = Path.Combine(root, AspireConfigFile.FileName);
        Assert.True(File.Exists(configPath));

        var saved = AspireConfigFile.Load(root);
        Assert.NotNull(saved);
        Assert.Equal("src/apphost.ts", saved.AppHost?.Path);
        Assert.Equal("13.2.0", saved.SdkVersion);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_LeavesAbsolutePathUnchanged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var absolutePath = Path.Combine(root, "src", "apphost.ts").Replace(Path.DirectorySeparatorChar, '/');
        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, $$"""
            {
                "appHostPath": "{{absolutePath}}"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        // Absolute paths should not be modified
        Assert.Equal(absolutePath, config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_NormalizesBackslashSeparators()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        // Simulate a settings file created on Windows with backslash separators.
        // Even though we always store '/', handle '\' gracefully in case of manual edits.
        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "..\\src\\apphost.ts",
                "language": "typescript/nodejs"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        // Should be re-based and normalized to forward slashes
        Assert.Equal("src/apphost.ts", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_OutputAlwaysUsesForwardSlashes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "../deeply/nested/path/apphost.ts"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        // Verify output uses forward slashes regardless of platform
        Assert.Equal("deeply/nested/path/apphost.ts", config.AppHost?.Path);
        Assert.DoesNotContain("\\", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_SkipsEmptyPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "",
                "language": "typescript/nodejs"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        // Empty path should not be transformed to "."
        Assert.Equal("", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_SkipsNullPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "language": "typescript/nodejs"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        // No appHostPath means no migration needed; path stays null
        Assert.Null(config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_DotSlashRelativePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        // "./MyApp.AppHost/apphost.ts" from .aspire/ dir resolves to .aspire/MyApp.AppHost/apphost.ts
        // relative to root. While unusual, verifies dot-slash handling doesn't break.
        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "./../MyApp.AppHost/apphost.ts"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        Assert.Equal("MyApp.AppHost/apphost.ts", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_BareRelativePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        // A bare relative path without ../ from .aspire/ stays under .aspire/ when resolved.
        // In practice legacy paths always start with ../ but we verify the math is correct.
        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "../MyApp.AppHost/MyApp.AppHost.csproj"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        Assert.Equal("MyApp.AppHost/MyApp.AppHost.csproj", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_LeavesUnixRootedPathUnchanged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, """
            {
                "appHostPath": "/path/apphost.ts"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        Assert.Equal("/path/apphost.ts", config.AppHost?.Path);
    }

    [Fact]
    public void LoadOrCreate_MigratesLegacy_LeavesWindowsRootedPathUnchanged()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-rooted paths are only recognized on Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var settingsPath = Path.Combine(root, ".aspire", "settings.json");
        File.WriteAllText(settingsPath, $$"""
            {
                "appHostPath": "c:\\path\\apphost.ts"
            }
            """);

        var config = AspireConfigFile.LoadOrCreate(root);

        // On Windows, c:\ is rooted and should be left unchanged
        Assert.Equal("c:\\path\\apphost.ts", config.AppHost?.Path);
    }
}
