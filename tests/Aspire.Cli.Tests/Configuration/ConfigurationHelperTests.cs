// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Configuration;

public class ConfigurationHelperTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteSettingsFileAtomically_CanRejectSymbolicLinks(bool dangling)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var target = Path.Combine(workspace.Path, "shared.json");
        if (!dangling)
        {
            await File.WriteAllTextAsync(target, "{}");
        }
        TestSymlinkHelper.TryCreateSymlink(path, target, isDirectory: false);

        await Assert.ThrowsAsync<IOException>(() => ConfigurationHelper.WriteSettingsFileAtomicallyAsync(
            path, new JsonObject { ["custom"] = "updated" }, CancellationToken.None, rejectSymbolicLinks: true));

        Assert.Equal(target, new FileInfo(path).LinkTarget);
        Assert.Equal(!dangling, File.Exists(target));
        if (!dangling)
        {
            Assert.Equal("{}", await File.ReadAllTextAsync(target));
        }
        Assert.Equal(dangling ? [path] : [path, target], Directory.GetFiles(workspace.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteSettingsFileAtomically_ValidationFailurePreservesDestinationAndCleansStaging(bool preserveOwnershipAndAcl)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string original = """{"custom":"original"}""";
        await File.WriteAllTextAsync(path, original);
        var failure = new IOException("The settings changed during staging.");
        var validations = 0;

        var error = await Assert.ThrowsAsync<IOException>(() => ConfigurationHelper.WriteSettingsFileAtomicallyAsync(
            path, new JsonObject { ["custom"] = "updated" }, CancellationToken.None,
            preserveOwnershipAndAcl: preserveOwnershipAndAcl, beforeReplace: token =>
            {
                token.ThrowIfCancellationRequested();
                validations++;
                Assert.Equal(2, Directory.GetFiles(workspace.Path).Length);
                throw failure;
            }));

        Assert.Same(failure, error);
        Assert.Equal(1, validations);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(workspace.Path));
    }

    [Fact]
    public async Task WriteSettingsFileAtomically_TrackedEditDoesNotOverwriteNewCompetingDestination()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string competing = """{"custom":"competing"}""";

        await Assert.ThrowsAsync<IOException>(() => ConfigurationHelper.WriteSettingsFileAtomicallyAsync(
            path, new JsonObject { ["custom"] = "updated" }, CancellationToken.None,
            beforeReplace: token => File.WriteAllTextAsync(path, competing, token)));

        Assert.Equal(competing, await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(workspace.Path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WriteSettingsFileAtomically_PreservesSymbolicLink(bool relativeTarget, bool linkedDirectory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var project = workspace.CreateDirectory("project");
        var shared = workspace.CreateDirectory("shared");
        var targetPath = Path.Combine(shared.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(targetPath, """{"custom":"original"}""");
        var targetDirectory = shared.FullName;
        if (linkedDirectory)
        {
            targetDirectory = Path.Combine(workspace.Path, "linked-shared");
            TestSymlinkHelper.TryCreateSymlink(targetDirectory, shared.FullName);
        }
        var path = Path.Combine(project.FullName, AspireConfigFile.FileName);
        var linkTarget = Path.Combine(targetDirectory, AspireConfigFile.FileName);
        if (relativeTarget)
        {
            linkTarget = Path.GetRelativePath(project.FullName, linkTarget);
        }
        TestSymlinkHelper.TryCreateSymlink(path, linkTarget, isDirectory: false);

        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, new JsonObject { ["custom"] = "updated" }, CancellationToken.None);

        Assert.Equal(linkTarget, new FileInfo(path).LinkTarget);
        Assert.Equal("updated", JsonNode.Parse(await File.ReadAllTextAsync(targetPath))!["custom"]!.GetValue<string>());
        Assert.Equal(await File.ReadAllTextAsync(targetPath), await File.ReadAllTextAsync(path));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(project.FullName)));
        Assert.Equal(targetPath, Assert.Single(Directory.GetFiles(shared.FullName)));
        if (linkedDirectory)
        {
            Assert.Equal(shared.FullName, new DirectoryInfo(targetDirectory).LinkTarget);
        }
    }

    [Fact]
    public async Task WriteSettingsFileAtomically_PreservesDanglingSymbolicLink()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var targetPath = Path.Combine(workspace.Path, "shared", AspireConfigFile.FileName);
        TestSymlinkHelper.TryCreateSymlink(path, targetPath, isDirectory: false);

        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, new JsonObject { ["custom"] = "updated" }, CancellationToken.None);

        Assert.Equal(targetPath, new FileInfo(path).LinkTarget);
        Assert.Equal("updated", JsonNode.Parse(await File.ReadAllTextAsync(targetPath))!["custom"]!.GetValue<string>());
        Assert.Equal(path, Assert.Single(Directory.GetFiles(workspace.Path)));
        Assert.Equal(targetPath, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(targetPath)!)));
    }

    [Fact]
    public async Task WriteSettingsFileAtomically_RejectsSymbolicLinkCycleWithoutChangingLinks()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var otherPath = Path.Combine(workspace.Path, "other.json");
        TestSymlinkHelper.TryCreateSymlink(path, otherPath, isDirectory: false);
        TestSymlinkHelper.TryCreateSymlink(otherPath, path, isDirectory: false);

        await Assert.ThrowsAsync<IOException>(() =>
            ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, new JsonObject { ["custom"] = "updated" }, CancellationToken.None));

        Assert.Equal(otherPath, new FileInfo(path).LinkTarget);
        Assert.Equal(path, new FileInfo(otherPath).LinkTarget);
        Assert.Equal(2, Directory.GetFiles(workspace.Path).Length);
    }

    [Theory]
    [InlineData(false, UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [InlineData(true, UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [InlineData(true, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    public async Task WriteSettingsFileAtomically_PreservesUnixPermissions(bool existing, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file permissions are not available on Windows.");
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        if (existing)
        {
            await File.WriteAllTextAsync(path, "{}");
            File.SetUnixFileMode(path, mode);
        }

        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, new JsonObject { ["custom"] = "updated" }, CancellationToken.None);

        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal("updated", JsonNode.Parse(await File.ReadAllTextAsync(path))!["custom"]!.GetValue<string>());
        Assert.Equal(path, Assert.Single(Directory.GetFiles(workspace.Path)));
    }

    [Fact]
    public async Task WriteSettingsFileAtomically_PreservesWindowsAccessRestrictions()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACLs are not available on this platform.");
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.Path, AspireConfigFile.FileName));
        await File.WriteAllTextAsync(file.FullName, "{}");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(security);
        var expected = file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(file.FullName, new JsonObject { ["custom"] = "updated" }, CancellationToken.None);

        Assert.Equal(expected, file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal("updated", JsonNode.Parse(await File.ReadAllTextAsync(file.FullName))!["custom"]!.GetValue<string>());
        Assert.Equal(file.FullName, Assert.Single(Directory.GetFiles(workspace.Path)));
    }

    private static IConfiguration BuildConfigurationFromSettingsFile(
        TemporaryWorkspace workspace, string content)
    {
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, content);

        var globalDir = workspace.CreateDirectory("global-aspire");
        var globalSettingsFile = new FileInfo(Path.Combine(globalDir.FullName, AspireConfigFile.FileName));

        var builder = new ConfigurationBuilder();
        ConfigurationHelper.RegisterSettingsFiles(builder, workspace.WorkspaceRoot, globalSettingsFile);
        return builder.Build();
    }

    [Fact]
    public void RegisterSettingsFiles_LoadsValidJson()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              "appHost": { "path": "MyApp.csproj" },
              "channel": "daily"
            }
            """);

        Assert.Equal("MyApp.csproj", config["appHost:path"]);
        Assert.Equal("daily", config["channel"]);
    }

    [Fact]
    public void RegisterSettingsFiles_HandlesJsonComments()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              // This is a comment
              "appHost": {
                "path": "MyApp.csproj" // inline comment
              },
              "channel": "stable"
            }
            """);

        Assert.Equal("MyApp.csproj", config["appHost:path"]);
        Assert.Equal("stable", config["channel"]);
    }

    [Fact]
    public void RegisterSettingsFiles_HandlesTrailingCommas()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              "appHost": { "path": "MyApp.csproj", },
              "channel": "stable",
            }
            """);

        Assert.Equal("MyApp.csproj", config["appHost:path"]);
    }

    [Fact]
    public void RegisterSettingsFiles_HandlesBlockComments()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              /* Block comment */
              "channel": "daily"
            }
            """);

        Assert.Equal("daily", config["channel"]);
    }

    [Fact]
    public void RegisterSettingsFiles_HandlesCommentsAndTrailingCommas()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var config = BuildConfigurationFromSettingsFile(workspace, """
            {
              // Full-line comment
              "appHost": {
                "path": "MyApp.csproj", // trailing comma after value
              },
              /* Block comment */
              "channel": "daily", // trailing comma
            }
            """);

        Assert.Equal("MyApp.csproj", config["appHost:path"]);
        Assert.Equal("daily", config["channel"]);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("legacy-local")]
    [InlineData("global")]
    public void RegisterSettingsFiles_ReadOnlyNormalizesInMemory(string location)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var globalDirectory = workspace.CreateDirectory("global-aspire");
        var globalSettingsFile = new FileInfo(Path.Combine(globalDirectory.FullName, AspireConfigFile.FileName));
        File.WriteAllText(globalSettingsFile.FullName, """
            {
              "appHost:path": "Global.csproj",
              "globalOnly:enabled": true
            }
            """);
        var settingsPath = location switch
        {
            "global" => globalSettingsFile.FullName,
            "legacy-local" => ConfigurationHelper.BuildPathToSettingsJsonFile(workspace.Path),
            _ => Path.Combine(workspace.Path, AspireConfigFile.FileName)
        };
        File.WriteAllText(settingsPath, """
            {
              // Flat keys must not overwrite existing nested values.
              "appHost:path": "Flat.csproj",
              "appHost": { "path": "Nested.csproj" },
              "features:enabled": true,
              "features:disabled": false,
              "settings:count": 42,
              "settings:nullable": null,
              "settings:values": [1, 2],
              "settings:object": { "enabled": true },
              "blocked:child": "ignored",
              "blocked": "existing",
              "empty:child": "filled",
              "empty": null,
              "replace:child": true,
              "replace": { "child": null },
              "settings.dot": "literal",
            }
            """);
        var originalFiles = Directory.GetFiles(workspace.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        var builder = new ConfigurationBuilder();
        ConfigurationHelper.RegisterSettingsFiles(builder, workspace.WorkspaceRoot, globalSettingsFile, persistNormalization: false);
        var config = builder.Build();

        Assert.Equal("Nested.csproj", config["appHost:path"]);
        Assert.True(config.GetValue<bool>("features:enabled"));
        Assert.False(config.GetValue<bool>("features:disabled"));
        Assert.Equal(42, config.GetValue<int>("settings:count"));
        Assert.Null(config["settings:nullable"]);
        Assert.Equal(["1", "2"], config.GetSection("settings:values").GetChildren().Select(child => child.Value));
        Assert.True(config.GetValue<bool>("settings:object:enabled"));
        Assert.Equal("existing", config["blocked"]);
        Assert.Empty(config.GetSection("blocked").GetChildren());
        Assert.Equal("filled", config["empty:child"]);
        Assert.True(config.GetValue<bool>("replace:child"));
        Assert.Equal("literal", config["settings.dot"]);
        if (location != "global")
        {
            Assert.True(config.GetValue<bool>("globalOnly:enabled"));
        }

        Assert.Equal(originalFiles.Keys.Order(), Directory.GetFiles(workspace.Path, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in originalFiles)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        // Ordinary startup still persists normalization and loads the same values.
        var normalBuilder = new ConfigurationBuilder();
        ConfigurationHelper.RegisterSettingsFiles(normalBuilder, workspace.WorkspaceRoot, globalSettingsFile);
        var normalConfig = normalBuilder.Build();
        Assert.Equal(config.AsEnumerable().OrderBy(pair => pair.Key), normalConfig.AsEnumerable().OrderBy(pair => pair.Key));
        var normalized = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        Assert.False(normalized.ContainsKey("features:enabled"));
        Assert.Equal(JsonValueKind.True, normalized["features"]!["enabled"]!.GetValueKind());
        Assert.Equal(JsonValueKind.False, normalized["features"]!["disabled"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, normalized["settings"]!["count"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Array, normalized["settings"]!["values"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Object, normalized["settings"]!["object"]!.GetValueKind());
        Assert.Null(normalized["settings"]!["nullable"]);
    }

    [Fact]
    public void TryNormalizeSettingsFile_PreservesBooleanTypes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        // File has a colon-separated key with a boolean value
        File.WriteAllText(settingsPath, """
            {
              "features:polyglotSupportEnabled": true,
              "features:showAllTemplates": false
            }
            """);

        var normalized = ConfigurationHelper.TryNormalizeSettingsFile(settingsPath);

        Assert.True(normalized);

        var json = JsonNode.Parse(File.ReadAllText(settingsPath));
        var polyglotNode = json!["features"]!["polyglotSupportEnabled"];
        var templatesNode = json!["features"]!["showAllTemplates"];
        Assert.Equal(JsonValueKind.True, polyglotNode!.GetValueKind());
        Assert.Equal(JsonValueKind.False, templatesNode!.GetValueKind());

        // Verify the file can be loaded by AspireConfigFile without error
        var config = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(config?.Features);
        Assert.True(config.Features["polyglotSupportEnabled"]);
        Assert.False(config.Features["showAllTemplates"]);
    }

    [Theory]
    [InlineData("{\r\n  \"a\": 1\r\n}", "\n", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\r\n  \"a\": 1\r\n}\r\n", "\n", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\n  \"a\": 1\n}", "\n", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\n  \"a\": 1\n}\n\n\n", "\n", "{\n  \"a\": 1\n}\n")]
    [InlineData("{\r\n  \"a\": 1\r\n}\r\n\r\n", "\n", "{\n  \"a\": 1\n}\n")]
    [InlineData("{}", "\n", "{}\n")]
    [InlineData("{\n  \"a\": 1\n}", "\r\n", "{\r\n  \"a\": 1\r\n}\r\n")]
    [InlineData("{\n  \"a\": 1\n}\n", "\r\n", "{\r\n  \"a\": 1\r\n}\r\n")]
    [InlineData("{\r\n  \"a\": 1\r\n}", "\r\n", "{\r\n  \"a\": 1\r\n}\r\n")]
    [InlineData("{\r\n  \"a\": 1\r\n}\r\n\r\n", "\r\n", "{\r\n  \"a\": 1\r\n}\r\n")]
    [InlineData("{\n  \"a\": 1,\r\n  \"b\": 2\n}", "\r\n", "{\r\n  \"a\": 1,\r\n  \"b\": 2\r\n}\r\n")]
    [InlineData("{}", "\r\n", "{}\r\n")]
    // Only CRLF and LF are rewritten: U+0085, U+2028 and U+2029 stay as they are, unlike with string.ReplaceLineEndings.
    [InlineData("{\n  \"a\": \"x\u0085y\u2028z\u2029w\"\n}", "\r\n", "{\r\n  \"a\": \"x\u0085y\u2028z\u2029w\"\r\n}\r\n")]
    public void NormalizeJsonFileContent_UsesNewLineAndSingleFinalNewline(string json, string newLine, string expected)
    {
        var normalized = ConfigurationHelper.NormalizeJsonFileContent(json, newLine);

        Assert.Equal(expected, normalized);
        Assert.Equal(expected, ConfigurationHelper.NormalizeJsonFileContent(normalized, newLine));
    }

    [Fact]
    public void EnsureFinalNewline_AddsOneOsNewLineToSerializedJson()
    {
        var settings = new JsonObject
        {
            ["appHost"] = new JsonObject { ["path"] = "MyApp.csproj" },
            ["channel"] = "daily"
        };

        // Serialized text is not normalized: System.Text.Json indents with the OS line break on its own and
        // only lacks the final newline, so that is all that is added.
        var json = JsonSerializer.Serialize(settings, JsonSourceGenerationContext.Default.JsonObject);

        LineEndingAssert.UsesOsLineBreaksAndSingleFinalNewline(ConfigurationHelper.EnsureFinalNewline(json));
    }

    // Both line breaks are covered on every OS, so a second newline is never added after an existing final one.
    [Theory]
    [InlineData("{}\n")]
    [InlineData("{}\r\n")]
    public void EnsureFinalNewline_KeepsExistingFinalNewline(string json)
    {
        Assert.Equal(json, ConfigurationHelper.EnsureFinalNewline(json));
    }

    [Fact]
    public void NormalizeJsonFileContent_ConvertsNonOsLineBreaksToOsLineBreaks()
    {
        var json = "{" + LineEndingAssert.NonOsNewLine + "  \"a\": 1" + LineEndingAssert.NonOsNewLine + "}";

        LineEndingAssert.UsesOsLineBreaksAndSingleFinalNewline(ConfigurationHelper.NormalizeJsonFileContent(json));
    }

    [Fact]
    public void WriteSettingsFile_WritesOsLineBreaksAndSingleFinalNewline()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nested", AspireConfigFile.FileName);
        var settings = new JsonObject
        {
            ["appHost"] = new JsonObject { ["path"] = "MyApp.csproj" },
            ["channel"] = "daily"
        };

        ConfigurationHelper.WriteSettingsFile(settingsPath, settings);

        LineEndingAssert.FileUsesOsLineBreaksAndSingleFinalNewline(settingsPath);
        Assert.Equal("daily", JsonNode.Parse(File.ReadAllText(settingsPath))!["channel"]!.GetValue<string>());
    }

    [Fact]
    public async Task WriteSettingsFileAsync_WritesOsLineBreaksAndSingleFinalNewline()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nested", AspireConfigFile.FileName);
        var settings = new JsonObject
        {
            ["appHost"] = new JsonObject { ["path"] = "MyApp.csproj" },
            ["channel"] = "daily"
        };

        await ConfigurationHelper.WriteSettingsFileAsync(settingsPath, settings);

        LineEndingAssert.FileUsesOsLineBreaksAndSingleFinalNewline(settingsPath);
        Assert.Equal("daily", JsonNode.Parse(File.ReadAllText(settingsPath))!["channel"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteSettingsFileAtomically_WritesOsLineBreaksAndSingleFinalNewline(bool existing)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        if (existing)
        {
            File.WriteAllText(settingsPath, "{" + LineEndingAssert.NonOsNewLine + "  \"channel\": \"stable\"" + LineEndingAssert.NonOsNewLine + "}");
        }
        var settings = new JsonObject
        {
            ["appHost"] = new JsonObject { ["path"] = "MyApp.csproj" },
            ["channel"] = "daily"
        };

        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(settingsPath, settings, CancellationToken.None);

        LineEndingAssert.FileUsesOsLineBreaksAndSingleFinalNewline(settingsPath);
        Assert.Equal("daily", JsonNode.Parse(File.ReadAllText(settingsPath))!["channel"]!.GetValue<string>());
    }

    [Fact]
    public void TryNormalizeSettingsFile_RewritesNonOsLineBreaksWithOsLineBreaks()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, "{" + LineEndingAssert.NonOsNewLine + "  \"features:showAllTemplates\": true" + LineEndingAssert.NonOsNewLine + "}");

        var normalized = ConfigurationHelper.TryNormalizeSettingsFile(settingsPath);

        Assert.True(normalized);
        LineEndingAssert.FileUsesOsLineBreaksAndSingleFinalNewline(settingsPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisterSettingsFiles_PreservesLiteralDictionaryKeysWithoutSource(bool persistNormalization)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var settingsPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string original = """
            {
              "features": { "feature:name": true },
              "profiles": {
                "default": {
                  "environmentVariables": { "Logging:LogLevel:Default": "Debug" }
                },
                "dev:local": { "applicationUrl": "https://localhost:1234" }
              },
              "custom": { "nested": { "literal:key": "preserved" } }
            }
            """;
        File.WriteAllText(settingsPath, original);
        var globalSettingsFile = new FileInfo(Path.Combine(workspace.Path, "global-settings.json"));
        using var configuration = new ConfigurationManager();

        ConfigurationHelper.RegisterSettingsFiles(configuration, workspace.WorkspaceRoot, globalSettingsFile, persistNormalization);

        Assert.Equal(original, File.ReadAllText(settingsPath));
        var config = AspireConfigFile.Load(workspace.Path)!;
        Assert.Null(config.ConfigSource);
        Assert.Equal(new Dictionary<string, bool> { ["feature:name"] = true }, config.Features);
        Assert.Equal(["default", "dev:local"], config.Profiles!.Keys);
        Assert.Equal(new Dictionary<string, string> { ["Logging:LogLevel:Default"] = "Debug" },
            config.Profiles["default"].EnvironmentVariables);
        Assert.Equal("https://localhost:1234", config.Profiles["dev:local"].ApplicationUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterSettingsFiles_NormalizesCompletePathsAcrossCaseEquivalentSections(bool persistNormalization)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var settingsPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string original = """
            {
              "DOCS": {
                "custom": "local",
                "API": { "other": "preserved" },
                "api": { "flag": null }
              },
              "docs": {
                "api": { "SITEMAPURL": "nested", "OBJECT": null }
              },
              "section": { "NODE": null },
              "SECTION": { "node": { "value": "nested" } },
              "docs:api:sitemapUrl": "flat",
              "DOCS:API:flag": true,
              "DOCS:API:OBJECT": { "enabled": true },
              "docs:api:object:enabled": false,
              "section:node": { "value": "flat" }
            }
            """;
        await File.WriteAllTextAsync(settingsPath, original);
        var globalSettingsFile = new FileInfo(Path.Combine(workspace.Path, "global-settings.json"));
        using var configuration = new ConfigurationManager();

        ConfigurationHelper.RegisterSettingsFiles(configuration, workspace.WorkspaceRoot, globalSettingsFile, persistNormalization);

        Assert.Equal("local", configuration["docs:custom"]);
        Assert.Equal("preserved", configuration["docs:api:other"]);
        Assert.Equal("nested", configuration["docs:api:sitemapUrl"]);
        Assert.True(configuration.GetValue<bool>("docs:api:flag"));
        Assert.True(configuration.GetValue<bool>("docs:api:object:enabled"));
        Assert.Equal("nested", configuration["section:node:value"]);
        var persisted = await File.ReadAllTextAsync(settingsPath);
        if (persistNormalization)
        {
            await Verify(persisted, "json").UseParameters(persistNormalization);
        }
        else
        {
            Assert.Equal(original, persisted);
        }
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(100_000)]
    public void NormalizeSettings_LargeSplitSectionsPreserveNestedValues(int propertyCount)
    {
        var nested = new JsonObject();
        var settings = new JsonObject
        {
            ["DOCS"] = new JsonObject { ["custom"] = "preserved" },
            ["docs"] = new JsonObject { ["api"] = nested }
        };
        for (var i = 0; i < propertyCount; i++)
        {
            nested.Add($"k{i}", i);
            settings.Add($"docs:api:k{i}", -i - 1);
        }
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(settings.ToJsonString()), 1, 4 * 1024 * 1024);
        var started = Stopwatch.GetTimestamp();

        Assert.True(ConfigurationHelper.NormalizeSettings(settings));

        var elapsed = Stopwatch.GetElapsedTime(started);
        outputHelper.WriteLine($"Normalized {propertyCount:N0} split-section collisions in {elapsed.TotalMilliseconds:N1} ms.");
        Assert.Equal(["DOCS", "docs"], settings.Select(property => property.Key));
        Assert.Equal("preserved", Assert.Single(settings["DOCS"]!.AsObject()).Value!.GetValue<string>());
        Assert.Equal(propertyCount, nested.Count);
        for (var i = 0; i < propertyCount; i++)
        {
            Assert.Equal(i, nested[$"k{i}"]!.GetValue<int>());
        }
        Assert.False(ConfigurationHelper.NormalizeSettings(settings));
    }

    [Theory]
    [InlineData("""{"DOCS":{"API":{"SITEMAPURL":"nested"}},"docs:api:other":"flat","docs:api:sitemapUrl":"flat"}""", "nested")]
    [InlineData("""{"DOCS":{"API":{"SITEMAPURL":null}},"docs:api:other":"flat","docs:api:sitemapUrl":"flat"}""", "flat")]
    public void NormalizeSettings_MergesMixedCaseSegmentsAndPreservesNestedPrecedence(string json, string expectedValue)
    {
        var settings = JsonNode.Parse(json)!.AsObject();

        Assert.True(ConfigurationHelper.NormalizeSettings(settings));

        Assert.Equal("DOCS", Assert.Single(settings).Key);
        var docs = settings["DOCS"]!.AsObject();
        Assert.Equal("API", Assert.Single(docs).Key);
        var api = docs["API"]!.AsObject();
        Assert.Equal(["SITEMAPURL", "other"], api.Select(property => property.Key));
        Assert.Equal(expectedValue, api["SITEMAPURL"]!.GetValue<string>());
        Assert.Equal("flat", api["other"]!.GetValue<string>());
        Assert.False(ConfigurationHelper.NormalizeSettings(settings));
    }

    [Fact]
    public async Task NormalizeSettings_PreservesNestedLiteralKeys()
    {
        var settings = JsonNode.Parse("""
            {
              "profiles": {
                "dev:local": {
                  "environmentVariables": { "Logging:LogLevel:Default": "Debug" }
                }
              },
              "features": { "feature:name": true },
              "custom": { "nested": { "literal:key": "preserved" } },
              "settings:object": { "nested:value": 42 },
              "settings:enabled": true
            }
            """)!.AsObject();

        Assert.True(ConfigurationHelper.NormalizeSettings(settings));

        Assert.False(ConfigurationHelper.NormalizeSettings(settings));
        await Verify(settings.ToJsonString(), "json");
    }

    [Fact]
    public async Task NormalizeSettings_PreservesParentCreationAndBlockedPaths()
    {
        var settings = JsonNode.Parse("""
            {
              "DOCS": null,
              "docs": { "keep": 1 },
              "docs:new": 2,
              "scalar": 7,
              "SCALAR": { "existing": true },
              "scalar:new": 9,
              "array": [1, 2],
              "array:new": 3,
              "emptyParent": { "": { "VALUE": null } },
              "emptyParent::value": true,
              ":leading": 4,
              "trailing:": 5,
              "repeated::leaf": 6,
              "fresh": null,
              "fresh:branch:leaf": 7,
              "fresh:object": { "enabled": true }
            }
            """)!.AsObject();
        var flatObject = settings["fresh:object"];

        Assert.True(ConfigurationHelper.NormalizeSettings(settings));

        Assert.NotSame(flatObject, settings["fresh"]!["object"]);
        Assert.Null(flatObject!.Parent);
        Assert.False(ConfigurationHelper.NormalizeSettings(settings));
        await Verify(settings.ToJsonString(), "json");
    }

    [Fact]
    public async Task NormalizeSettings_PreservesPropertyOrderAndFirstMatchingCasing()
    {
        var settings = JsonNode.Parse("""
            {
              "docs:new:leaf": 1,
              "keep": { "value": "unchanged" },
              "DOCS": {
                "API": { "SITEMAPURL": "nested" },
                "api": { "custom": "preserved" }
              },
              "DOCS:API:SITEMAPURL": "flat",
              "docs:new:LEAF": 2,
              "newSection:sub:Value": null,
              "NEWSECTION:sub:value": 3,
              "tail": "last",
              "blocked": 5,
              "blocked:key": 6
            }
            """)!.AsObject();
        var retained = settings["keep"];
        var docs = settings["DOCS"];

        Assert.True(ConfigurationHelper.NormalizeSettings(settings));

        Assert.Same(retained, settings["keep"]);
        Assert.Same(docs, settings["DOCS"]);
        Assert.False(ConfigurationHelper.NormalizeSettings(settings));
        await Verify(settings.ToJsonString(), "json");
    }

    [Theory]
    [InlineData(1_000, false)]
    [InlineData(10_000, false)]
    [InlineData(100_000, false)]
    [InlineData(1_000, true)]
    [InlineData(10_000, true)]
    [InlineData(100_000, true)]
    public void NormalizeSettings_LargeFlatConfigurationPreservesEveryValue(int propertyCount, bool hasExistingSection)
    {
        var settings = new JsonObject();
        if (hasExistingSection)
        {
            var existing = new JsonObject();
            for (var i = 0; i < propertyCount; i++)
            {
                existing.Add($"x{i}", -i);
            }
            settings.Add("DOCS", existing);
        }
        for (var i = 0; i < propertyCount; i++)
        {
            settings.Add($"docs:k{i}", i);
        }
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(settings.ToJsonString()), 1, 4 * 1024 * 1024);
        var started = Stopwatch.GetTimestamp();

        Assert.True(ConfigurationHelper.NormalizeSettings(settings));

        var elapsed = Stopwatch.GetElapsedTime(started);
        outputHelper.WriteLine($"Normalized {propertyCount:N0} flat keys (existing section: {hasExistingSection}) in {elapsed.TotalMilliseconds:N1} ms.");
        var docs = Assert.Single(settings);
        Assert.Equal(hasExistingSection ? "DOCS" : "docs", docs.Key);
        var properties = docs.Value!.AsObject();
        Assert.Equal(propertyCount * (hasExistingSection ? 2 : 1), properties.Count);
        for (var i = 0; i < propertyCount; i++)
        {
            Assert.Equal(i, properties[$"k{i}"]!.GetValue<int>());
            if (hasExistingSection)
            {
                Assert.Equal(-i, properties[$"x{i}"]!.GetValue<int>());
            }
        }
        Assert.False(ConfigurationHelper.NormalizeSettings(settings));
    }

    [Fact]
    public void GetConfigRootDirectory_UsesNearestAspireConfigDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var configRoot = workspace.CreateDirectory("project");
        Directory.CreateDirectory(Path.Combine(configRoot.FullName, "nested", "apphost"));
        File.WriteAllText(Path.Combine(configRoot.FullName, AspireConfigFile.FileName), "{}");

        var resolvedRoot = ConfigurationHelper.GetConfigRootDirectory(
            new DirectoryInfo(Path.Combine(configRoot.FullName, "nested", "apphost")));

        Assert.Equal(configRoot.FullName, resolvedRoot.FullName);
    }

    [Fact]
    public void GetWorkspaceAspireDirectory_UsesLegacySettingsParentDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var appHostDirectory = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "nested", "apphost"));
        appHostDirectory.Create();

        var aspireDirectory = ConfigurationHelper.GetWorkspaceAspireDirectory(appHostDirectory);

        Assert.Equal(
            Path.Combine(workspace.WorkspaceRoot.FullName, AspireJsonConfiguration.SettingsFolder),
            aspireDirectory.FullName);
    }

    [Fact]
    public void RegisterSettingsFiles_DoesNotMigrateLegacySettingsOnStartup()
    {
        // Read commands (aspire ls, ps, doctor, --version) must not silently write
        // aspire.config.json to disk when a workspace only has the legacy
        // .aspire/settings.json. Earlier versions migrated eagerly here
        // (https://github.com/microsoft/aspire/issues/15488), but that violated the
        // "read commands don't mutate the working tree" contract reported in
        // https://github.com/microsoft/aspire/issues/17615.
        //
        // Migration is now deferred to commands that already mutate the workspace
        // (aspire run/add/init/update/etc.). Startup must register the legacy file
        // directly so legacy settings remain readable from IConfiguration without
        // materializing aspire.config.json.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var legacyDir = workspace.CreateDirectory(AspireJsonConfiguration.SettingsFolder);
        var legacySettingsPath = Path.Combine(legacyDir.FullName, AspireJsonConfiguration.FileName);
        File.WriteAllText(legacySettingsPath, """
            {
              "appHostPath": "MyApp.csproj",
              "channel": "stable"
            }
            """);

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        Assert.False(File.Exists(aspireConfigPath), "Precondition: aspire.config.json should not yet exist.");

        var globalDir = workspace.CreateDirectory("global-aspire");
        var globalSettingsFile = new FileInfo(Path.Combine(globalDir.FullName, AspireConfigFile.FileName));

        var builder = new ConfigurationBuilder();
        ConfigurationHelper.RegisterSettingsFiles(builder, workspace.WorkspaceRoot, globalSettingsFile);

        Assert.False(
            File.Exists(aspireConfigPath),
            "Startup must not materialize aspire.config.json — that would be a silent write from a read command path.");

        // Legacy values must remain readable via their flat key. Consumers reading the
        // hierarchical key go through AppHostPathConfigurationPolicy.TryFindAppHostPathKey,
        // which falls back to the legacy "appHostPath" key.
        var config = builder.Build();
        Assert.Equal("MyApp.csproj", config["appHostPath"]);
        Assert.Equal("stable", config["channel"]);
    }

    [Fact]
    public void RegisterSettingsFiles_DoesNotOverwriteExistingAspireConfigJson()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // Both files present: the workspace was already migrated but the legacy file was
        // retained (this is the documented transition state — see AspireConfigFile.LoadOrCreate
        // and https://github.com/microsoft/aspire/issues/15239). Startup must continue to
        // prefer the new file and must not touch either file from a read-command path.
        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        var existingContent = """
            {
              "appHost": { "path": "Current.csproj" },
              "channel": "daily"
            }
            """;
        File.WriteAllText(aspireConfigPath, existingContent);

        var legacyDir = workspace.CreateDirectory(AspireJsonConfiguration.SettingsFolder);
        var legacySettingsPath = Path.Combine(legacyDir.FullName, AspireJsonConfiguration.FileName);
        File.WriteAllText(legacySettingsPath, """
            { "appHostPath": "Stale.csproj", "channel": "stable" }
            """);

        var globalDir = workspace.CreateDirectory("global-aspire");
        var globalSettingsFile = new FileInfo(Path.Combine(globalDir.FullName, AspireConfigFile.FileName));

        var builder = new ConfigurationBuilder();
        ConfigurationHelper.RegisterSettingsFiles(builder, workspace.WorkspaceRoot, globalSettingsFile);

        Assert.Equal(existingContent, File.ReadAllText(aspireConfigPath));

        var config = builder.Build();
        Assert.Equal("Current.csproj", config["appHost:path"]);
        Assert.Equal("daily", config["channel"]);
    }

    [Fact]
    public void RegisterSettingsFiles_UnparseableLegacyFileDoesNotCreateAspireConfigJson()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var legacyDir = workspace.CreateDirectory(AspireJsonConfiguration.SettingsFolder);
        var legacySettingsPath = Path.Combine(legacyDir.FullName, AspireJsonConfiguration.FileName);
        File.WriteAllText(legacySettingsPath, "{ this is not valid json");

        var globalDir = workspace.CreateDirectory("global-aspire");
        var globalSettingsFile = new FileInfo(Path.Combine(globalDir.FullName, AspireConfigFile.FileName));

        var builder = new ConfigurationBuilder();
        // AddSettingsFile (JSON configuration provider) may throw on malformed JSON when the
        // configuration is built, but RegisterSettingsFiles itself must not produce a
        // partially-written aspire.config.json on the way through.
        var ex = Record.Exception(() =>
            ConfigurationHelper.RegisterSettingsFiles(builder, workspace.WorkspaceRoot, globalSettingsFile));

        var migratedPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        Assert.False(File.Exists(migratedPath), "Unparseable legacy file must not produce an aspire.config.json on disk.");
        Assert.True(ex is null or InvalidOperationException, $"Unexpected exception type: {ex?.GetType().FullName}");
    }
}
