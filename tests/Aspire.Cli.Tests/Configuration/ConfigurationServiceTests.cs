// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Configuration;

public class ConfigurationServiceTests(ITestOutputHelper outputHelper)
{
    private static (ConfigurationService Service, string SettingsFilePath) CreateService(
        TemporaryWorkspace workspace,
        string? existingContent = null)
    {
        var globalSettingsDir = workspace.CreateDirectory(".aspire-global");
        var globalSettingsFile = new FileInfo(Path.Combine(globalSettingsDir.FullName, AspireConfigFile.FileName));

        var settingsFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        if (existingContent is not null)
        {
            File.WriteAllText(settingsFilePath, existingContent);
        }

        var logsDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "logs"));
        var executionContext = new CliExecutionContext(
            workspace.WorkspaceRoot,
            new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives")),
            new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")),
            logsDir,
            "test.log");

        var configBuilder = new ConfigurationBuilder();
        var configuration = configBuilder.Build();

        var logger = NullLogger<ConfigurationService>.Instance;
        var service = new ConfigurationService(configuration, executionContext, globalSettingsFile, logger);

        return (service, settingsFilePath);
    }

    [Fact]
    public async Task SetConfigurationAsync_WorksWithJsonComments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var contentWithComments = """
            {
              // This is a comment about the apphost
              "appHost": {
                "path": "MyApp.csproj" // path to the project
              }
            }
            """;

        var (service, settingsFilePath) = CreateService(workspace, contentWithComments);

        await service.SetConfigurationAsync("channel", "daily", isGlobal: false);

        var result = File.ReadAllText(settingsFilePath);
        Assert.Contains("daily", result);
        Assert.Contains("appHost", result);
    }

    [Fact]
    public async Task SetConfigurationAsync_WorksWithTrailingCommas()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var contentWithTrailingCommas = """
            {
              "appHost": {
                "path": "MyApp.csproj",
              },
              "channel": "stable",
            }
            """;

        var (service, settingsFilePath) = CreateService(workspace, contentWithTrailingCommas);

        await service.SetConfigurationAsync("features.polyglotSupportEnabled", "true", isGlobal: false);

        var result = File.ReadAllText(settingsFilePath);
        Assert.Contains("polyglotSupportEnabled", result);
    }

    [Fact]
    public async Task SetConfigurationAsync_WorksWithCommentsAndTrailingCommas()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var content = """
            {
              // Comment
              "appHost": {
                "path": "MyApp.csproj", // trailing comma
              },
            }
            """;

        var (service, settingsFilePath) = CreateService(workspace, content);

        await service.SetConfigurationAsync("channel", "daily", isGlobal: false);

        var result = File.ReadAllText(settingsFilePath);
        Assert.Contains("daily", result);
    }

    [Fact]
    public async Task SetConfigurationAsync_CreatesNewFile_WhenNoneExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Delete the sentinel .aspire/settings.json so there is truly no settings file
        var sentinelPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        if (File.Exists(sentinelPath))
        {
            File.Delete(sentinelPath);
        }

        var (service, settingsFilePath) = CreateService(workspace);

        await service.SetConfigurationAsync("channel", "staging", isGlobal: false);

        Assert.True(File.Exists(settingsFilePath));
        var result = File.ReadAllText(settingsFilePath);
        Assert.Contains("staging", result);
    }

    [Fact]
    public async Task SetConfigurationAsync_HandlesEmptyFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "");

        await service.SetConfigurationAsync("channel", "daily", isGlobal: false);

        var result = File.ReadAllText(settingsFilePath);
        Assert.Contains("daily", result);
    }

    [Fact]
    public async Task DeleteConfigurationAsync_WorksWithJsonComments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var contentWithComments = """
            {
              // Comment
              "channel": "daily",
              "appHost": { "path": "MyApp.csproj" }
            }
            """;

        var (service, settingsFilePath) = CreateService(workspace, contentWithComments);

        var deleted = await service.DeleteConfigurationAsync("channel", isGlobal: false);

        Assert.True(deleted);
        var result = File.ReadAllText(settingsFilePath);
        Assert.DoesNotContain("daily", result);
        Assert.Contains("appHost", result);
    }

    [Fact]
    public async Task DeleteConfigurationAsync_ReturnsFalse_WhenFileDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (service, _) = CreateService(workspace);

        var deleted = await service.DeleteConfigurationAsync("channel", isGlobal: false);

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteConfigurationAsync_ReturnsFalse_WhenFileIsEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (service, _) = CreateService(workspace, "");

        var deleted = await service.DeleteConfigurationAsync("channel", isGlobal: false);

        Assert.False(deleted);
    }

    [Fact]
    public async Task GetAllConfigurationAsync_ParsesCommentsCorrectly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var contentWithComments = """
            {
              // This config has comments
              "channel": "daily",
              "features": {
                "polyglotSupportEnabled": true // enabled for testing
              }
            }
            """;

        var (service, _) = CreateService(workspace, contentWithComments);

        var config = await service.GetAllConfigurationAsync();

        Assert.Contains("channel", config.Keys);
        Assert.Equal("daily", config["channel"]);
    }

    [Fact]
    public async Task SetConfigurationAsync_SetsNestedValues()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "{}");

        await service.SetConfigurationAsync("appHost.path", "MyApp/MyApp.csproj", isGlobal: false);

        var result = File.ReadAllText(settingsFilePath);
        Assert.Contains("appHost", result);
        Assert.Contains("MyApp/MyApp.csproj", result);
    }

    [Fact]
    public async Task SetConfigurationAsync_WritesBooleanStringAsJsonString()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "{}");

        await service.SetConfigurationAsync("features.polyglotSupportEnabled", "true", isGlobal: false);

        // Value is written as a JSON string "true", not a JSON boolean true.
        // The FlexibleBooleanConverter handles parsing "true" -> bool on read.
        var json = JsonNode.Parse(File.ReadAllText(settingsFilePath));
        var node = json!["features"]!["polyglotSupportEnabled"];
        Assert.Equal(JsonValueKind.String, node!.GetValueKind());
        Assert.Equal("true", node.GetValue<string>());

        // Verify round-trip through AspireConfigFile.Load still works
        var config = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(config?.Features);
        Assert.True(config.Features["polyglotSupportEnabled"]);
    }

    [Fact]
    public async Task SetConfigurationAsync_ChannelWithBooleanLikeValue_StaysAsString()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "{}");

        // "true" is a valid channel value and must remain a string in JSON
        // to avoid corrupting the string-typed Channel property.
        await service.SetConfigurationAsync("channel", "true", isGlobal: false);

        // Must be a JSON string "true", not a JSON boolean true
        var json = JsonNode.Parse(File.ReadAllText(settingsFilePath));
        var node = json!["channel"];
        Assert.Equal(JsonValueKind.String, node!.GetValueKind());
        Assert.Equal("true", node.GetValue<string>());

        // Verify it round-trips correctly through AspireConfigFile.Load
        var config = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(config);
        Assert.Equal("true", config.Channel);
    }

    [Fact]
    public async Task SetConfigurationAsync_WritesStringValueAsString()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "{}");

        await service.SetConfigurationAsync("channel", "daily", isGlobal: false);

        var json = JsonNode.Parse(File.ReadAllText(settingsFilePath));
        var node = json!["channel"];
        Assert.Equal(JsonValueKind.String, node!.GetValueKind());
        Assert.Equal("daily", node.GetValue<string>());
    }
}
