// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class NativeAgentConfigurationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Theory]
    [InlineData("copilot", ".vscode", "servers")]
    [InlineData("claude", ".vscode", "servers")]
    [InlineData("copilot", ".github", "mcpServers")]
    [InlineData("claude", ".github", "mcpServers")]
    public async Task ProjectMcp_PreservesExistingEditorOrLegacyEntry(string agent, string directory, string container)
    {
        var path = Path.Combine(_context.Project.FullName, directory, "mcp.json");
        var existing = $$"""{"{{container}}": { "aspire": { "command":"aspire", "args":["agent","mcp"], "env": { "CUSTOM":"preserved" } } } }""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();
        var timestamp = File.GetLastWriteTimeUtc(path);
        var client = _context.Environments.Single(scanner => scanner.Id == agent);

        var results = await _context.ConfigureNativeAsync(_context.Request(
            AgentConfigurationScope.Project, [client], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Skipped, Assert.Single(results).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.False(File.Exists(Path.Combine(_context.Project.FullName, ".mcp.json")));
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("""{"servers":{"aspire":{"command":"aspire","args":["agent","mcp"]}}}""", "Skipped")]
    [InlineData("""{"servers":{"aspire":{"disabled":true}}}""", "Skipped")]
    [InlineData("""{"servers":{"aspire":{"command":"different-server","args":[]}}}""", "Blocked")]
    [InlineData("""{"disabledMcpServers":["aspire"]}""", "Skipped")]
    [InlineData("{malformed", "Blocked")]
    public async Task ProjectMcp_DoesNotBypassExistingEditorConfiguration(string existing, string status)
    {
        var path = Path.Combine(_context.Project.FullName, ".vscode", "mcp.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request(
            AgentConfigurationScope.Project, [_context.Copilot], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(Enum.Parse<AgentConfigurationStatus>(status), Assert.Single(results).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.False(File.Exists(Path.Combine(_context.Project.FullName, ".mcp.json")));
    }

    [Fact]
    public async Task Copilot_RegistersOneSharedSourcePerScope()
    {
        IAgentEnvironmentScanner[] clients = [_context.Copilot];

        var projectResults = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.Project, clients)).DefaultTimeout();
        Assert.Equal(AgentConfigurationScope.Project, Assert.Single(projectResults).Scope);
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
        var userResults = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.User, clients)).DefaultTimeout();
        var results = projectResults.Concat(userResults).ToArray();

        Assert.Equal(2, results.Length);
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

        var results = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.User, [_context.ClaudeCode])).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Unchanged, results.Single(result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(userPath).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(userPath));
        await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.Project, [_context.ClaudeCode])).DefaultTimeout();
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

        var results = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.Project, [client])).DefaultTimeout();

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

        var results = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.Project, [_context.Copilot])).DefaultTimeout();

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

        var results = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.Project, [_context.Copilot])).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Fact]
    public async Task Claude_RespectsLocalDisableAndManagedMarketplacePolicy()
    {
        var local = Path.Combine(_context.Project.FullName, ".claude", "settings.local.json");
        const string disabled = """{"enabledPlugins":{"aspire@aspire-skills":false}}""";
        await AgentConfigurationTestContext.WriteAsync(local, disabled).DefaultTimeout();
        var request = _context.Request(AgentConfigurationScope.Project, [_context.ClaudeCode]);
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
        var request = _context.Request(AgentConfigurationScope.Project,
            [_context.Copilot, _context.ClaudeCode],
            skills: false, mcp: true);

        var projectResults = await _context.ConfigureNativeAsync(request).DefaultTimeout();
        Assert.Equal([_context.Copilot, _context.ClaudeCode], Assert.Single(projectResults).Environments);
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
        var userResults = await _context.ConfigureNativeAsync(request with { Scope = AgentConfigurationScope.User }).DefaultTimeout();
        var results = projectResults.Concat(userResults).ToArray();

        Assert.Equal(3, results.Length);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var shared = results.Single(result => result.TargetPath == Path.Combine(_context.Project.FullName, ".mcp.json"));
        Assert.Equal([_context.Copilot, _context.ClaudeCode], shared.Environments);
        Assert.Equal(
            new[]
            {
                Path.Combine(_context.Project.FullName, ".mcp.json"),
                Path.Combine(_context.CopilotDirectory, "mcp-config.json"),
                _context.ClaudeMcpFile
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

        var results = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.Project, [_context.Copilot, _context.ClaudeCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, results.Single(result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Single(results);
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
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

        var result = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.User, [_context.Copilot], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, result.Single(target => target.Scope is AgentConfigurationScope.User).Status);
        Assert.Single(result);
        var projectResult = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.Project, [_context.Copilot], skills: false, mcp: true)).DefaultTimeout();
        Assert.Equal(AgentConfigurationStatus.Skipped, Assert.Single(projectResult).Status);
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

        var results = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.User, [_context.ClaudeCode], mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.True(File.Exists(Path.Combine(custom, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_context.Home.FullName, ".claude.json")));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(state).DefaultTimeout())!.AsObject();
        config.Remove("mcpServers");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(existing), config));
    }

    [Fact]
    public async Task Copilot_UserOverride_PreservesTheRequestedScope()
    {
        _context.SetVariable("COPILOT_HOME", Path.Combine(_context.Project.FullName, ".github", "copilot"));
        var request = _context.Request(AgentConfigurationScope.User, [_context.Copilot]);

        var results = await _context.ConfigureNativeAsync(request).DefaultTimeout();
        var repeated = await _context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.Equal(AgentConfigurationScope.User, results[0].Scope);
        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(repeated).Status);
    }

    [Fact]
    public async Task Claude_ManagedOnlyAllowlistIsNotOverriddenByAUserAllowlist()
    {
        var managed = Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.json");
        await AgentConfigurationTestContext.WriteAsync(managed,
            """{"allowManagedMcpServersOnly":true,"allowedMcpServers":[{"serverName":"aspire"}]}""").DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.ClaudeDirectory, "settings.json"),
            """{"allowedMcpServers":[]}""").DefaultTimeout();

        var results = await _context.ConfigureNativeAsync(_context.Request(AgentConfigurationScope.Project, [_context.ClaudeCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
    }
    public void Dispose() => _context.Dispose();

}
