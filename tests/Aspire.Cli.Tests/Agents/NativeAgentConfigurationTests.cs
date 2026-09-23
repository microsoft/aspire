// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class NativeAgentConfigurationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Fact]
    public async Task Copilot_RegistersOneSharedSourcePerScope()
    {
        IAgentEnvironmentScanner[] clients = [_context.Copilot];

        var results = await _context.ConfigureNativeAsync(_context.Request(clients)).DefaultTimeout();

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
            Assert.Equal(clients, result.Environments);
        });
        Assert.Equal([AgentConfigurationScope.Project, AgentConfigurationScope.User], results.Select(result => result.Scope));
        var project = await File.ReadAllTextAsync(Path.Combine(_context.Project.FullName, ".github", "copilot", "settings.json")).DefaultTimeout();
        var user = await File.ReadAllTextAsync(Path.Combine(_context.CopilotDirectory, "settings.json")).DefaultTimeout();
        Assert.Equal(project, user);
        Assert.Empty(_context.SkillInstaller.Requests);
        Assert.Equal(0, _context.HookInstaller.Calls);
        Assert.False(Directory.Exists(Path.Combine(_context.Project.FullName, ".vscode")));

        await Verify(project, "json");
    }

    [Fact]
    public async Task Claude_PreservesPinsPreferencesAndExistingBytes()
    {
        var userPath = Path.Combine(_context.ClaudeDirectory, "settings.json");
        const string existing = """
            {
              // Keep this comment on a semantic no-op.
              "permissions": { "deny": ["Bash(git push:*)"] },
              "autoUpdate": false,
              "extraKnownMarketplaces": {
                "aspire-skills": {
                  "source": { "source": "github", "repo": "microsoft/aspire-skills", "ref": "v0.0.3" },
                  "autoUpdate": false
                }
              },
              "enabledPlugins": { "aspire@aspire-skills": true },
            }
            """;
        await AgentConfigurationTestContext.WriteAsync(userPath, existing).DefaultTimeout();
        File.SetLastWriteTimeUtc(userPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(userPath);

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.ClaudeCode])).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Unchanged, results.Single(result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(userPath).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(userPath));
        var project = await File.ReadAllTextAsync(Path.Combine(_context.Project.FullName, ".claude", "settings.json")).DefaultTimeout();
        await Verify(project, "json");
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task NativePlugins_DoNotOverrideAnExplicitGlobalDisable(string clientName)
    {
        var client = _context.Environments.Single(client => client.Id == clientName);
        var userDirectory = client == _context.ClaudeCode ? _context.ClaudeDirectory : _context.CopilotDirectory;
        var path = Path.Combine(userDirectory, "settings.json");
        const string existing = """{"enabledPlugins":{"aspire@aspire-skills":false},"autoUpdate":false}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([client])).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Skipped, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_context.Project.FullName));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"extraKnownMarketplaces":[]}""")]
    [InlineData("""{"extraKnownMarketplaces":{"aspire-skills":null}}""")]
    [InlineData("""{"enabledPlugins":{"aspire@aspire-skills":"true"}}""")]
    [InlineData("""{"enabledPlugins":{"aspire@aspire-skills":true,"aspire@aspire-skills":false}}""")]
    public async Task NativePlugins_BlockMalformedShapesWithoutOverwritingOtherTargets(string existing)
    {
        var path = Path.Combine(_context.CopilotDirectory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.Copilot])).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_context.Project.FullName));
    }

    [Fact]
    public async Task NativePlugins_DoNotReplaceAConflictingMarketplace()
    {
        var path = Path.Combine(_context.CopilotDirectory, "settings.json");
        const string existing = """{"extraKnownMarketplaces":{"aspire-skills":{"source":{"source":"directory","path":"./private-marketplace"}}}}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.Copilot])).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Fact]
    public async Task Claude_RespectsLocalDisableAndManagedMarketplacePolicy()
    {
        var local = Path.Combine(_context.Project.FullName, ".claude", "settings.local.json");
        const string disabled = """{"enabledPlugins":{"aspire@aspire-skills":false}}""";
        await AgentConfigurationTestContext.WriteAsync(local, disabled).DefaultTimeout();
        var request = _context.Request([_context.ClaudeCode]);
        var disabledResults = await _context.ConfigureNativeAsync(request).DefaultTimeout();
        Assert.All(disabledResults, result => Assert.Equal(AgentConfigurationStatus.Skipped, result.Status));
        Assert.Equal(disabled, await File.ReadAllTextAsync(local).DefaultTimeout());
        File.Delete(local);

        var managed = Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.json");
        const string policy = """{"strictKnownMarketplaces":[]}""";
        await AgentConfigurationTestContext.WriteAsync(managed, policy).DefaultTimeout();

        var blocked = await _context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.All(blocked, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(policy, await File.ReadAllTextAsync(managed).DefaultTimeout());
        Assert.False(File.Exists(Path.Combine(_context.ClaudeDirectory, "settings.json")));
    }

    [Fact]
    public async Task Mcp_UsesSharedCopilotClaudeProjectAndSeparateNativeUserFiles()
    {
        var request = _context.Request(
            [_context.Copilot, _context.ClaudeCode, _context.VsCode],
            skills: false, mcp: true);

        var results = await _context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(5, results.Count);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var shared = results.Single(result => result.TargetPath == Path.Combine(_context.Project.FullName, ".mcp.json"));
        Assert.Equal([_context.Copilot, _context.ClaudeCode], shared.Environments);
        Assert.Equal(
            new[]
            {
                Path.Combine(_context.Project.FullName, ".mcp.json"),
                Path.Combine(_context.CopilotDirectory, "mcp-config.json"),
                _context.ClaudeMcpFile,
                Path.Combine(_context.Project.FullName, ".vscode", "mcp.json"),
                Path.Combine(_context.VsCodeUserDirectory(false), "mcp.json")
            }.Order(AgentPath.Comparer),
            results.Select(result => result.TargetPath).Order(AgentPath.Comparer));
        Assert.Empty(_context.SkillInstaller.Requests);
        await Verify(await File.ReadAllTextAsync(shared.TargetPath).DefaultTimeout(), "json");
    }

    [Fact]
    public async Task SharedMcpEntry_DoesNotBypassClaudesManagedPolicy()
    {
        var managed = Path.Combine(_context.ClaudeManagedDirectory, "managed-mcp.json");
        const string policy = """{"mcpServers":{}}""";
        await AgentConfigurationTestContext.WriteAsync(managed, policy).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.Copilot, _context.ClaudeCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, results.Single(result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Configured, results.Single(result => result.TargetPath == Path.Combine(_context.CopilotDirectory, "mcp-config.json")).Status);
        Assert.Equal(policy, await File.ReadAllTextAsync(managed).DefaultTimeout());
        Assert.False(File.Exists(Path.Combine(_context.Project.FullName, ".mcp.json")));
    }

    [Fact]
    public async Task Mcp_RepairsOnlyTheDeprecatedPrefix()
    {
        var path = Path.Combine(_context.CopilotDirectory, "mcp-config.json");
        await AgentConfigurationTestContext.WriteAsync(path, """
            {
              "mcpServers": {
                "other": { "command": "other-server", "args": [] },
                "aspire": {
                  "type": "local",
                  "command": "aspire",
                  "args": ["mcp", "start", "--project", "AppHost.cs"],
                  "env": { "DOTNET_ROOT": "pinned-sdk", "OTHER": "preserved" },
                  "tools": ["list_resources"]
                }
              }
            }
            """).DefaultTimeout();

        var result = await _context.ConfigureNativeAsync(_context.Request([_context.Copilot], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, result.Single(target => target.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal(AgentConfigurationStatus.Skipped, result.Single(target => target.Scope is AgentConfigurationScope.Project).Status);
        Assert.False(File.Exists(Path.Combine(_context.Project.FullName, ".github", "mcp.json")));
        await Verify(await File.ReadAllTextAsync(path).DefaultTimeout(), "json");
    }

    [Fact]
    public async Task Claude_UsesDocumentedConfigDirectoryOverrideForSettingsAndUserMcp()
    {
        var custom = Path.Combine(_context.Home.FullName, "claude-work");
        _context.SetVariable("CLAUDE_CONFIG_DIR", custom);
        var state = Path.Combine(custom, ".claude.json");
        const string existing = """{"oauthAccount":{"accountUuid":"preserved"},"projects":{},"theme":"dark"}""";
        await AgentConfigurationTestContext.WriteAsync(state, existing).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.ClaudeCode], mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.True(File.Exists(Path.Combine(custom, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_context.Home.FullName, ".claude.json")));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(state).DefaultTimeout())!.AsObject();
        config.Remove("mcpServers");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(existing), config));
    }

    [Fact]
    public async Task VsCode_UsesDetectedInsidersAndExistingProfile()
    {
        var user = _context.VsCodeUserDirectory(insiders: true);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(user, "profiles", "work-profile", "settings.json"), "{}").DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(user, "profiles", "builtin", "settings.json"), "{}").DefaultTimeout();
        var request = _context.Request([_context.VsCode], skills: false, mcp: true,
            detections: [new(AgentClientKind.VsCode, "1.110.0", IsInsiders: true)]);

        var results = await _context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.True(File.Exists(Path.Combine(user, "mcp.json")));
        Assert.True(File.Exists(Path.Combine(user, "profiles", "work-profile", "mcp.json")));
        Assert.False(Directory.Exists(_context.VsCodeUserDirectory(insiders: false)));
    }

    [Theory]
    [InlineData("VSCODE_PORTABLE")]
    [InlineData("VSCODE_APPDATA")]
    public async Task VsCode_HonorsVerifiedUserDataOverrides(string variable)
    {
        var custom = Path.Combine(_context.Home.FullName, "custom-code");
        _context.SetVariable(variable, custom);

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode], skills: false, mcp: true)).DefaultTimeout();

        var userPath = Path.Combine(custom, variable == "VSCODE_PORTABLE" ? "user-data" : "Code", "User", "mcp.json");
        Assert.Equal(userPath, results.Single(result => result.Scope is AgentConfigurationScope.User).TargetPath);
        Assert.True(File.Exists(userPath));
    }

    [Fact]
    public async Task Copilot_CollapsesProjectAndUserAliases()
    {
        _context.SetVariable("COPILOT_HOME", Path.Combine(_context.Project.FullName, ".github", "copilot"));
        var request = _context.Request([_context.Copilot]);

        var results = await _context.ConfigureNativeAsync(request).DefaultTimeout();
        var repeated = await _context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.Equal(AgentConfigurationScope.User, results[0].Scope);
        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(repeated).Status);
    }

    [Fact]
    public async Task VsCode_RegistersOnlyItsOwnUserMarketplace()
    {
        var path = Path.Combine(_context.VsCodeUserDirectory(false), "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path,
            """{"editor.fontSize":14,"chat.plugins.marketplaces":["example/tools"],"chat.pluginLocations":{"/custom-plugin":false}}""").DefaultTimeout();
        var request = _context.Request([_context.VsCode]);

        var results = await _context.ConfigureNativeAsync(request).DefaultTimeout();
        var contents = await File.ReadAllTextAsync(path).DefaultTimeout();
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);
        var repeated = await _context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(path, Assert.Single(results).TargetPath);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.All(repeated, result => Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status));
        Assert.Equal(contents, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.False(Directory.Exists(_context.CopilotDirectory));
        Assert.False(Directory.Exists(_context.ClaudeDirectory));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        await Verify(contents, "json");
    }

    [Fact]
    public async Task VsCodeAndCopilot_ConfigureOnlyTheirOwnSettings()
    {

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.Copilot, _context.VsCode])).DefaultTimeout();

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var project = Assert.Single(results, result => result.Scope is AgentConfigurationScope.Project);
        Assert.Equal([_context.Copilot], project.Environments);
        Assert.Equal(
            new[] { Path.Combine(_context.CopilotDirectory, "settings.json"), Path.Combine(_context.VsCodeUserDirectory(false), "settings.json") }.Order(),
            results.Where(result => result.Scope is AgentConfigurationScope.User).Select(result => result.TargetPath).Order());
    }

    [Theory]
    [InlineData("""{"chat.plugins.enabled":false}""", "Skipped")]
    [InlineData("""{"chat.plugins.strictMarketplaces":true}""", "Blocked")]
    [InlineData("""{"chat.plugins.enabledPlugins":{"aspire@aspire-skills":false}}""", "Blocked")]
    [InlineData("""{"chat.plugins.enabledPlugins":[]}""", "Blocked")]
    [InlineData("{broken", "Blocked")]
    public async Task VsCode_PreservesDisabledOrManagedNativeSettings(string content, string status)
    {
        var path = Path.Combine(_context.VsCodeUserDirectory(false), "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, content).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode])).DefaultTimeout();

        Assert.Single(results);
        Assert.All(results, result => Assert.Equal(Enum.Parse<AgentConfigurationStatus>(status), result.Status));
        Assert.Equal(content, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.False(Directory.Exists(_context.CopilotDirectory));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"other@marketplace":false}""")]
    [InlineData("""{"aspire@aspire-skills":true}""")]
    public async Task VsCode_EnabledPluginsDoesNotRequireAnAspireEntry(string plugins)
    {
        var path = Path.Combine(_context.VsCodeUserDirectory(false), "settings.json");
        var original = JsonNode.Parse($$"""{"chat.plugins.enabledPlugins":{{plugins}},"editor.fontSize":14}""")!.AsObject();
        await AgentConfigurationTestContext.WriteAsync(path, original.ToJsonString()).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode])).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        var actual = JsonNode.Parse(await File.ReadAllTextAsync(path).DefaultTimeout())!.AsObject();
        Assert.Equal(["microsoft/aspire-skills"], actual["chat.plugins.marketplaces"]!.AsArray().Select(value => value!.GetValue<string>()));
        actual.Remove("chat.plugins.marketplaces");
        Assert.True(JsonNode.DeepEquals(original, actual));
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("[]", false)]
    [InlineData("""[{"source":"github","repo":"microsoft/aspire-skills"}]""", true)]
    [InlineData("""[{"source":"github","repo":"other/marketplace"}]""", false)]
    [InlineData("""[{"source":"git","url":"https://github.com/microsoft/aspire-skills.git"}]""", true)]
    [InlineData("""[{"source":"url","url":"git@github.com:microsoft/aspire-skills.git"}]""", true)]
    [InlineData("""[{"source":"github","repo":"microsoft/aspire-skills","ref":"v0.0.2"}]""", false)]
    [InlineData("""[{"source":"github","repo":"microsoft/aspire-skills","path":"plugins"}]""", false)]
    [InlineData("""[{"source":"hostPattern","hostPattern":"^github\\.com$"}]""", true)]
    [InlineData("""[{"source":"hostPattern","hostPattern":"["}]""", false)]
    public async Task VsCode_StrictMarketplacesUsesTheNativeAllowlist(string allowlist, bool permitted)
    {
        var path = Path.Combine(_context.VsCodeUserDirectory(false), "settings.json");
        var content = $$"""{"chat.plugins.strictMarketplaces":{{allowlist}},"editor.fontSize":14}""";
        await AgentConfigurationTestContext.WriteAsync(path, content).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode])).DefaultTimeout();

        Assert.Equal(permitted ? AgentConfigurationStatus.Configured : AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        var actual = await File.ReadAllTextAsync(path).DefaultTimeout();
        if (permitted)
        {
            var root = JsonNode.Parse(actual)!.AsObject();
            Assert.Equal(["microsoft/aspire-skills"], root["chat.plugins.marketplaces"]!.AsArray().Select(value => value!.GetValue<string>()));
            root.Remove("chat.plugins.marketplaces");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(content), root));
        }
        else
        {
            Assert.Equal(content, actual);
        }
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("release", true)]
    [InlineData("Release", false)]
    public async Task VsCode_StrictMarketplacesPreservesAndChecksExistingSourcePins(string allowedRef, bool permitted)
    {
        var path = Path.Combine(_context.VsCodeUserDirectory(false), "settings.json");
        var content = $$"""
            {
              "chat.plugins.marketplaces": ["https://github.com/microsoft/aspire-skills.git#release"],
              "chat.plugins.strictMarketplaces": [{"source":"github","repo":"microsoft/aspire-skills","ref":"{{allowedRef}}"}]
            }
            """;
        await AgentConfigurationTestContext.WriteAsync(path, content).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode])).DefaultTimeout();

        Assert.Equal(permitted ? AgentConfigurationStatus.Unchanged : AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Equal(content, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Theory]
    [InlineData("microsoft/aspire-skills#v0.0.2")]
    [InlineData("https://github.com/microsoft/aspire-skills.git")]
    [InlineData("git@github.com:microsoft/aspire-skills.git")]
    public async Task VsCode_PreservesExistingNativeMarketplaceSources(string marketplace)
    {
        var path = Path.Combine(_context.VsCodeUserDirectory(false), "settings.json");
        var content = $$"""{"chat.plugins.marketplaces":["example/tools","{{marketplace}}"]}""";
        await AgentConfigurationTestContext.WriteAsync(path, content).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode])).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(results).Status);
        Assert.Equal(content, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VsCode_RespectsClaudeWorkspacePinsAndDisabledRecommendations(bool disabled)
    {
        var path = Path.Combine(_context.Project.FullName, ".claude", "settings.json");
        var content = $$$$"""{"extraKnownMarketplaces":{"aspire-skills":{"source":{"source":"github","repo":"microsoft/aspire-skills","ref":"v0.0.2"}}},"enabledPlugins":{"aspire@aspire-skills":{{{{(!disabled).ToString().ToLowerInvariant()}}}}}}""";
        await AgentConfigurationTestContext.WriteAsync(path, content).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode])).DefaultTimeout();

        Assert.Equal(content, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Equal(AgentConfigurationStatus.Skipped, Assert.Single(results).Status);
        Assert.False(File.Exists(Path.Combine(_context.VsCodeUserDirectory(false), "settings.json")));
        Assert.False(Directory.Exists(_context.CopilotDirectory));
    }

    [Fact]
    public async Task VsCode_RegistersMarketplacesForInsidersProfilesWithoutChangingTheirActivation()
    {
        var user = _context.VsCodeUserDirectory(true);
        var profile = Path.Combine(user, "profiles", "work-profile", "settings.json");
        await AgentConfigurationTestContext.WriteAsync(profile, """{"chat.pluginLocations":{"/disabled-plugin":false}}""").DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode],
            detections: [new(AgentClientKind.VsCode, "1.120.0-insider", true)])).DefaultTimeout();

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(profile).DefaultTimeout())!;
        Assert.Equal(["microsoft/aspire-skills"], settings["chat.plugins.marketplaces"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.False(settings["chat.pluginLocations"]!["/disabled-plugin"]!.GetValue<bool>());
        Assert.False(Directory.Exists(_context.VsCodeUserDirectory(false)));
        Assert.False(Directory.Exists(_context.CopilotDirectory));
    }

    [Fact]
    public async Task Claude_ManagedOnlyAllowlistIsNotOverriddenByAUserAllowlist()
    {
        var managed = Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.json");
        await AgentConfigurationTestContext.WriteAsync(managed,
            """{"allowManagedMcpServersOnly":true,"allowedMcpServers":[{"serverName":"aspire"}]}""").DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.ClaudeDirectory, "settings.json"),
            """{"allowedMcpServers":[]}""").DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.ClaudeCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
    }

    [Fact]
    public async Task VsCode_RelativeAppDataOverrideUsesOriginalVsCodeDirectory()
    {
        _context.SetVariable("VSCODE_APPDATA", "relative-data");
        _context.SetVariable("VSCODE_CWD", _context.Workspace.WorkspaceRoot.FullName);

        var results = await _context.ConfigureNativeAsync(_context.Request([_context.VsCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(Path.Combine(_context.Workspace.WorkspaceRoot.FullName, "relative-data", "Code", "User", "mcp.json"),
            results.Single(result => result.Scope is AgentConfigurationScope.User).TargetPath);
    }
    public void Dispose() => _context.Dispose();

}
