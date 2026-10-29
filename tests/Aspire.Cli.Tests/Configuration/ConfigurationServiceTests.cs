// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
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

        var executionContext = workspace.CreateExecutionContext();

        var configBuilder = new ConfigurationBuilder();
        var configuration = configBuilder.Build();

        var logger = NullLogger<ConfigurationService>.Instance;
        var service = new ConfigurationService(configuration, executionContext, globalSettingsFile, logger);

        return (service, settingsFilePath);
    }

    [Fact]
    public async Task SetConfigurationAsync_MigratesLegacySettingsAndProfilesWithRequestedEdit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (service, destination) = CreateService(workspace);
        var legacyPath = Path.Combine(workspace.Path, ".aspire", "settings.json");
        const string original = """
            {
                "appHostPath": "../original/AppHost.csproj",
                "language": "csharp",
                "sdkVersion": "17.0.0",
                "features": { "defaultWatchEnabled": false }
            }
            """;
        const string profiles = """
            {
                "profiles": {
                    "default": {
                        "applicationUrl": "https://localhost:5001",
                        "environmentVariables": { "EXAMPLE": "value" }
                    }
                }
            }
            """;
        var profilesPath = Path.Combine(workspace.Path, "apphost.run.json");
        await File.WriteAllTextAsync(legacyPath, original);
        await File.WriteAllTextAsync(profilesPath, profiles);

        await service.SetConfigurationAsync("appHost.path", "updated/AppHost.csproj");

        await Verify(await File.ReadAllTextAsync(destination), "json");
        Assert.Equal(original, await File.ReadAllTextAsync(legacyPath));
        Assert.Equal(profiles, await File.ReadAllTextAsync(profilesPath));
    }

    [Theory]
    [InlineData("set")]
    [InlineData("delete")]
    [InlineData("source-add")]
    [InlineData("source-remove")]
    public async Task ConfigurationMutations_PreserveLinuxAccessAcl(string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux POSIX ACL validation.");
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (service, _) = CreateService(workspace);
        var path = service.GetSettingsFilePath(isGlobal: true);
        await File.WriteAllTextAsync(path, """{"custom":"original","templateSources":{"company":{"repository":"https://example.test/templates"}}}""");
        // The ACL mask makes the group mode r--, but group::--- denies the owning group access.
        TestPosixAcl.Set(path, "user::rw-,user:65534:r--,group::---,mask::r--,other::---");
        var acl = TestPosixAcl.Get(path);
        var mode = File.GetUnixFileMode(path);
        var store = new TemplateSourceStore(service, workspace.CreateExecutionContext(), NullLogger<TemplateSourceStore>.Instance);

        switch (mutation)
        {
            case "set":
                await service.SetConfigurationAsync("custom", "updated", isGlobal: true);
                break;
            case "delete":
                Assert.True(await service.DeleteConfigurationAsync("custom", isGlobal: true));
                break;
            case "source-add":
                await store.AddAsync("added", new JsonObject { ["repository"] = "https://example.test/added" }, CancellationToken.None);
                break;
            case "source-remove":
                Assert.True(await store.RemoveAsync("company", CancellationToken.None));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Equal(acl, TestPosixAcl.Get(path));
        Assert.Equal(mode, File.GetUnixFileMode(path));
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal(mutation == "delete" ? null : mutation == "set" ? "updated" : "original", settings["custom"]?.GetValue<string>());
        Assert.Equal(mutation switch
        {
            "source-add" => ["company", "added"],
            "source-remove" => [],
            _ => new[] { "company" }
        }, settings["templateSources"]?.AsObject().Select(source => source.Key).ToArray() ?? []);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfigurationMutations_UnavailableLockPreservesFile(bool global, bool delete)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string original = """{ /* unchanged */ "features:defaultWatchEnabled": true, }""";
        var (service, _) = CreateService(workspace, original);
        var path = service.GetSettingsFilePath(global);
        await File.WriteAllTextAsync(path, original);
        var cache = workspace.CreateExecutionContext().CacheDirectory;
        cache.Create();
        await File.WriteAllTextAsync(Path.Combine(cache.FullName, "workspace-config-locks"), "Blocks lock directory creation.");

        await Assert.ThrowsAsync<IOException>(() => delete
            ? service.DeleteConfigurationAsync("features.defaultWatchEnabled", global)
            : service.SetConfigurationAsync("features.defaultWatchEnabled", "false", global));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfigurationMutations_WaitForFileLockAndPreserveRefreshedSettings(bool global, bool delete)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (service, _) = CreateService(workspace, """{"custom":"original"}""");
        var path = service.GetSettingsFilePath(global);
        await File.WriteAllTextAsync(path, """{"custom":"original"}""");
        Task mutation;
        using (var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
            workspace.CreateExecutionContext(), new FileInfo(path), NullLogger.Instance, CancellationToken.None))
        {
            Assert.NotNull(configLock);
            mutation = delete
                ? service.DeleteConfigurationAsync("custom", global)
                : service.SetConfigurationAsync("custom", "edited", global);
            await Assert.ThrowsAsync<TimeoutException>(() => mutation.WaitAsync(TimeSpan.FromSeconds(1)));

            // Simulate the refresh's atomic publication while it owns this same file lock.
            await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, JsonNode.Parse("""
                {
                  "custom": "original",
                  "docs": { "llmsTxtUrl": "https://updated.example/llms.txt" },
                  "configSource": {
                    "repository": "https://git.example/configuration",
                    "path": "aspire.config.json",
                    "appliedVersion": "version-2"
                  }
                }
                """)!.AsObject(), CancellationToken.None);
        }
        await mutation.DefaultTimeout();

        var settings = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(delete ? ["configSource", "docs"] : new[] { "configSource", "custom", "docs" }, settings.Select(p => p.Key).Order());
        if (!delete)
        {
            Assert.Equal("edited", settings["custom"]!.GetValue<string>());
        }
        Assert.Equal("https://updated.example/llms.txt", settings["docs"]!["llmsTxtUrl"]!.GetValue<string>());
        Assert.Equal("version-2", settings["configSource"]!["appliedVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task SetConfigurationAsync_WorksWithJsonComments()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // The workspace has .aspire/settings.json (the legacy sentinel) but no
        // aspire.config.json. FindNearestSettingsFile will find the sentinel and
        // write to it.
        var legacySettingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.json");
        var (service, _) = CreateService(workspace);

        await service.SetConfigurationAsync("channel", "staging", isGlobal: false);

        Assert.True(File.Exists(legacySettingsPath));
        var result = File.ReadAllText(legacySettingsPath);
        Assert.Contains("staging", result);
    }

    [Fact]
    public async Task SetConfigurationAsync_RewritesNonOsLineBreaksWithOsLineBreaksAndSingleFinalNewline()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "{\n  \"appHost\": {\n    \"path\": \"MyApp.csproj\"\n  }\n}\n".ReplaceLineEndings(LineEndingAssert.NonOsNewLine));

        await service.SetConfigurationAsync("channel", "daily", isGlobal: false);

        LineEndingAssert.FileUsesOsLineBreaksAndSingleFinalNewline(settingsFilePath);
    }

    [Fact]
    public async Task SetConfigurationAsync_HandlesEmptyFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "");

        await service.SetConfigurationAsync("channel", "daily", isGlobal: false);

        var result = File.ReadAllText(settingsFilePath);
        Assert.Contains("daily", result);
    }

    [Fact]
    public async Task DeleteConfigurationAsync_RewritesNonOsLineBreaksWithOsLineBreaksAndSingleFinalNewline()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "{\n  \"channel\": \"daily\",\n  \"appHost\": {\n    \"path\": \"MyApp.csproj\"\n  }\n}\n".ReplaceLineEndings(LineEndingAssert.NonOsNewLine));

        var deleted = await service.DeleteConfigurationAsync("channel", isGlobal: false);

        Assert.True(deleted);
        LineEndingAssert.FileUsesOsLineBreaksAndSingleFinalNewline(settingsFilePath);
    }

    [Fact]
    public async Task DeleteConfigurationAsync_WorksWithJsonComments()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, _) = CreateService(workspace);

        var deleted = await service.DeleteConfigurationAsync("channel", isGlobal: false);

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteConfigurationAsync_ReturnsFalse_WhenFileIsEmpty()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, _) = CreateService(workspace, "");

        var deleted = await service.DeleteConfigurationAsync("channel", isGlobal: false);

        Assert.False(deleted);
    }

    [Fact]
    public async Task GetAllConfigurationAsync_ParsesCommentsCorrectly()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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
    public async Task GetConfigurationFromDirectoryAsync_WithContinueSearchWhenKeyMissing_WalksUpWhenNearestConfigDoesNotContainKey()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, _) = CreateService(
            workspace,
            """
            {
              "sdk": {
                "version": "10.0.0-preview.5.26311.1"
              }
            }
            """);

        var srcDirectory = workspace.WorkspaceRoot.CreateSubdirectory("src");
        await File.WriteAllTextAsync(
            Path.Combine(srcDirectory.FullName, AspireConfigFile.FileName),
            """
            {
              "packages": {
                "Aspire.Hosting.Redis": ""
              }
            }
            """);

        var value = await service.GetConfigurationFromDirectoryAsync("sdk.version", srcDirectory, continueSearchWhenKeyMissing: true);

        Assert.Equal("10.0.0-preview.5.26311.1", value);
    }

    [Fact]
    public async Task GetConfigurationFromDirectoryAsync_WhenNearestConfigDoesNotContainKey_DoesNotReadParentConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, _) = CreateService(
            workspace,
            """
            {
              "channel": "daily"
            }
            """);

        var srcDirectory = workspace.WorkspaceRoot.CreateSubdirectory("src");
        await File.WriteAllTextAsync(
            Path.Combine(srcDirectory.FullName, AspireConfigFile.FileName),
            """
            {
              "language": "csharp"
            }
            """);

        var value = await service.GetConfigurationFromDirectoryAsync("channel", srcDirectory);

        Assert.Null(value);
    }

    [Fact]
    public async Task GetConfigurationFromDirectoryAsync_WhenLocalConfigDoesNotContainKey_FallsBackToGlobalConfig()
    {
        const string configuredImage = "example.com/aspire-tunnel:global";

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, _) = CreateService(
            workspace,
            """
            {
              "language": "csharp"
            }
            """);

        await service.SetConfigurationAsync(
            AspireConfigContainerTunnel.BaseImageConfigPath,
            configuredImage,
            isGlobal: true);

        var value = await service.GetConfigurationFromDirectoryAsync(
            AspireConfigContainerTunnel.BaseImageConfigPath,
            workspace.WorkspaceRoot);

        Assert.Equal(configuredImage, value);
    }

    [Fact]
    public async Task GetConfigurationFromDirectoryAsync_FindsNearestParentConfigWhenStartDirectoryHasNoConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, _) = CreateService(
            workspace,
            """
            {
              "channel": "daily"
            }
            """);

        var srcDirectory = workspace.WorkspaceRoot.CreateSubdirectory("src");

        var value = await service.GetConfigurationFromDirectoryAsync("channel", srcDirectory);

        Assert.Equal("daily", value);
    }

    [Fact]
    public async Task SetConfigurationAsync_SetsNestedValues()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "{}");

        await service.SetConfigurationAsync("appHost.path", "MyApp/MyApp.csproj", isGlobal: false);

        var result = File.ReadAllText(settingsFilePath);
        Assert.Contains("appHost", result);
        Assert.Contains("MyApp/MyApp.csproj", result);
    }

    [Fact]
    public async Task SetConfigurationAsync_WritesBooleanStringAsJsonString()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (service, settingsFilePath) = CreateService(workspace, "{}");

        await service.SetConfigurationAsync("channel", "daily", isGlobal: false);

        var json = JsonNode.Parse(File.ReadAllText(settingsFilePath));
        var node = json!["channel"];
        Assert.Equal(JsonValueKind.String, node!.GetValueKind());
        Assert.Equal("daily", node.GetValue<string>());
    }
}
