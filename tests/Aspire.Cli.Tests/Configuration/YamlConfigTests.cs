// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Configuration;

public class YamlConfigTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Load_ReturnsConfig_WhenYamlFileExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        File.WriteAllText(configPath, """
            appHost:
              path: MyApp/MyApp.csproj
            channel: daily
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Equal("MyApp/MyApp.csproj", result.AppHost?.Path);
        Assert.Equal("daily", result.Channel);
        Assert.Equal(AspireConfigFile.FileNameYaml, result.SourceFileName);
    }

    [Fact]
    public void Load_ReturnsConfig_WhenYmlFileExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYml);
        File.WriteAllText(configPath, """
            appHost:
              path: MyApp/MyApp.csproj
            channel: stable
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Equal("MyApp/MyApp.csproj", result.AppHost?.Path);
        Assert.Equal("stable", result.Channel);
        Assert.Equal(AspireConfigFile.FileNameYml, result.SourceFileName);
    }

    [Fact]
    public void Load_ParsesFullYamlConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        File.WriteAllText(configPath, """
            appHost:
              path: src/AppHost/AppHost.csproj
              language: typescript/nodejs
            sdk:
              version: "13.2.0"
            channel: daily
            features:
              polyglotSupportEnabled: true
              showAllTemplates: false
            profiles:
              default:
                applicationUrl: "https://localhost:17000;http://localhost:15000"
                environmentVariables:
                  ASPNETCORE_ENVIRONMENT: Development
                  DOTNET_DASHBOARD_OTLP_ENDPOINT_URL: "https://localhost:21000"
            packages:
              Aspire.Hosting.Redis: "13.2.0"
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Equal("src/AppHost/AppHost.csproj", result.AppHost?.Path);
        Assert.Equal("typescript/nodejs", result.AppHost?.Language);
        Assert.Equal("13.2.0", result.SdkVersion);
        Assert.Equal("daily", result.Channel);
        Assert.NotNull(result.Features);
        Assert.True(result.Features["polyglotSupportEnabled"]);
        Assert.False(result.Features["showAllTemplates"]);
        Assert.NotNull(result.Profiles);
        Assert.True(result.Profiles.ContainsKey("default"));
        Assert.Equal("https://localhost:17000;http://localhost:15000", result.Profiles["default"].ApplicationUrl);
        Assert.Equal("Development", result.Profiles["default"].EnvironmentVariables?["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("https://localhost:21000", result.Profiles["default"].EnvironmentVariables?["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"]);
        Assert.NotNull(result.Packages);
        Assert.Equal("13.2.0", result.Packages["Aspire.Hosting.Redis"]);
    }

    [Fact]
    public void Load_ThrowsOnInvalidYaml()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        File.WriteAllText(configPath, """
            appHost:
              path: [invalid yaml
                this: is broken
            """);

        Assert.Throws<InvalidOperationException>(() => AspireConfigFile.Load(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void Load_ThrowsWhenMultipleFormatsExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), "{}");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml), "channel: daily");

        var ex = Assert.Throws<InvalidOperationException>(() => AspireConfigFile.Load(workspace.WorkspaceRoot.FullName));
        Assert.Contains("Multiple configuration files", ex.Message);
    }

    [Fact]
    public void Load_ThrowsWhenBothYamlAndYmlExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml), "channel: daily");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYml), "channel: stable");

        var ex = Assert.Throws<InvalidOperationException>(() => AspireConfigFile.Load(workspace.WorkspaceRoot.FullName));
        Assert.Contains("Multiple configuration files", ex.Message);
    }

    [Fact]
    public void Exists_ReturnsTrue_WhenYamlFileExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml), "channel: daily");

        Assert.True(AspireConfigFile.Exists(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void Exists_ReturnsTrue_WhenYmlFileExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYml), "channel: daily");

        Assert.True(AspireConfigFile.Exists(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void Save_WritesYaml_WhenSourceWasYaml()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = new AspireConfigFile
        {
            SourceFileName = AspireConfigFile.FileNameYaml,
            AppHost = new AspireConfigAppHost { Path = "src/AppHost/AppHost.csproj" },
            Channel = "daily"
        };

        config.Save(workspace.WorkspaceRoot.FullName);

        var filePath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        Assert.True(File.Exists(filePath));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName)));

        var content = File.ReadAllText(filePath);
        Assert.Contains("appHost", content);
        Assert.Contains("src/AppHost/AppHost.csproj", content);
        Assert.Contains("daily", content);
        // YAML should not contain JSON braces
        Assert.DoesNotContain("{", content);
    }

    [Fact]
    public void Save_WritesJson_WhenSourceWasJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = new AspireConfigFile
        {
            SourceFileName = AspireConfigFile.FileName,
            AppHost = new AspireConfigAppHost { Path = "App.csproj" },
        };

        config.Save(workspace.WorkspaceRoot.FullName);

        var filePath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        Assert.True(File.Exists(filePath));

        var content = File.ReadAllText(filePath);
        Assert.Contains("{", content); // JSON format
    }

    [Fact]
    public void Save_WritesJson_WhenSourceIsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var config = new AspireConfigFile
        {
            AppHost = new AspireConfigAppHost { Path = "App.csproj" },
        };

        config.Save(workspace.WorkspaceRoot.FullName);

        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName)));
    }

    [Fact]
    public void RoundTrip_YamlLoadAndSave()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Write initial YAML
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        File.WriteAllText(configPath, """
            appHost:
              path: App.csproj
            channel: daily
            profiles:
              default:
                applicationUrl: "https://localhost:5001"
                environmentVariables:
                  ASPNETCORE_ENVIRONMENT: Development
            """);

        // Load from YAML
        var config = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(config);
        Assert.Equal("App.csproj", config.AppHost?.Path);

        // Modify and save back as YAML
        config.Channel = "stable";
        config.Save(workspace.WorkspaceRoot.FullName);

        // Reload and verify
        var reloaded = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        Assert.Equal("App.csproj", reloaded.AppHost?.Path);
        Assert.Equal("stable", reloaded.Channel);
        Assert.Equal("https://localhost:5001", reloaded.Profiles?["default"].ApplicationUrl);
        Assert.Equal("Development", reloaded.Profiles?["default"].EnvironmentVariables?["ASPNETCORE_ENVIRONMENT"]);
    }

    [Fact]
    public void Load_HandlesYamlComments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        File.WriteAllText(configPath, """
            # This is a comment
            appHost:
              path: MyApp.csproj  # inline comment
            channel: daily
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Equal("MyApp.csproj", result.AppHost?.Path);
        Assert.Equal("daily", result.Channel);
    }

    [Fact]
    public void Load_ReturnsEmptyConfig_WhenYamlIsEmptyMapping()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        File.WriteAllText(configPath, "{}");

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Null(result.AppHost);
        Assert.Null(result.Channel);
    }

    [Fact]
    public void LoadOrCreate_LoadsYamlConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        File.WriteAllText(configPath, """
            appHost:
              path: App.csproj
            """);

        var result = AspireConfigFile.LoadOrCreate(workspace.WorkspaceRoot.FullName);

        Assert.Equal("App.csproj", result.AppHost?.Path);
        Assert.Equal(AspireConfigFile.FileNameYaml, result.SourceFileName);
    }

    [Fact]
    public void Load_HandlesQuotedStringsInYaml()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileNameYaml);
        File.WriteAllText(configPath, """
            sdk:
              version: "13.2.0"
            channel: "daily"
            """);

        var result = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Equal("13.2.0", result.SdkVersion);
        Assert.Equal("daily", result.Channel);
    }

    [Fact]
    public void IsYamlFile_ReturnsCorrectResults()
    {
        Assert.True(AspireConfigFile.IsYamlFile("aspire.config.yaml"));
        Assert.True(AspireConfigFile.IsYamlFile("aspire.config.yml"));
        Assert.True(AspireConfigFile.IsYamlFile("ASPIRE.CONFIG.YAML"));
        Assert.False(AspireConfigFile.IsYamlFile("aspire.config.json"));
        Assert.False(AspireConfigFile.IsYamlFile("settings.json"));
    }
}
