// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Configuration;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class NativeAgentConfigurationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Copilot_RegistersOneSharedSourcePerScope()
    {
        using var context = new AgentConfigurationTestContext(output);
        AgentClientKind[] clients = [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp, AgentClientKind.VsCode];

        var results = await context.ConfigureNativeAsync(context.Request(clients)).DefaultTimeout();

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
            Assert.Equal(clients, result.Clients);
        });
        Assert.Equal([AgentConfigurationScope.Project, AgentConfigurationScope.User], results.Select(result => result.Scope));
        var project = await File.ReadAllTextAsync(Path.Combine(context.Project.FullName, ".github", "copilot", "settings.json")).DefaultTimeout();
        var user = await File.ReadAllTextAsync(Path.Combine(context.Paths.CopilotDirectory, "settings.json")).DefaultTimeout();
        Assert.Equal(project, user);
        Assert.Empty(context.SkillInstaller.Requests);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.False(Directory.Exists(Path.Combine(context.Project.FullName, ".vscode")));

        await Verify(project, "json");
    }

    [Fact]
    public async Task Claude_PreservesPinsPreferencesAndExistingBytes()
    {
        using var context = new AgentConfigurationTestContext(output);
        var userPath = Path.Combine(context.Paths.ClaudeDirectory, "settings.json");
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

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.ClaudeCode])).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Unchanged, results.Single(result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(userPath).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(userPath));
        var project = await File.ReadAllTextAsync(Path.Combine(context.Project.FullName, ".claude", "settings.json")).DefaultTimeout();
        await Verify(project, "json");
    }

    [Theory]
    [InlineData(nameof(AgentClientKind.CopilotCli))]
    [InlineData(nameof(AgentClientKind.ClaudeCode))]
    public async Task NativePlugins_DoNotOverrideAnExplicitGlobalDisable(string clientName)
    {
        var client = Enum.Parse<AgentClientKind>(clientName);
        using var context = new AgentConfigurationTestContext(output);
        var userDirectory = client is AgentClientKind.ClaudeCode ? context.Paths.ClaudeDirectory : context.Paths.CopilotDirectory;
        var path = Path.Combine(userDirectory, "settings.json");
        const string existing = """{"enabledPlugins":{"aspire@aspire-skills":false},"autoUpdate":false}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([client])).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Skipped, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Project.FullName));
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
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Paths.CopilotDirectory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.CopilotApp])).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Project.FullName));
    }

    [Fact]
    public async Task NativePlugins_DoNotReplaceAConflictingMarketplace()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Paths.CopilotDirectory, "settings.json");
        const string existing = """{"extraKnownMarketplaces":{"aspire-skills":{"source":{"source":"directory","path":"./private-marketplace"}}}}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.CopilotCli])).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Fact]
    public async Task Claude_RespectsLocalDisableAndManagedMarketplacePolicy()
    {
        using var context = new AgentConfigurationTestContext(output);
        var local = Path.Combine(context.Project.FullName, ".claude", "settings.local.json");
        const string disabled = """{"enabledPlugins":{"aspire@aspire-skills":false}}""";
        await AgentConfigurationTestContext.WriteAsync(local, disabled).DefaultTimeout();
        var request = context.Request([AgentClientKind.ClaudeCode]);
        var disabledResults = await context.ConfigureNativeAsync(request).DefaultTimeout();
        Assert.All(disabledResults, result => Assert.Equal(AgentConfigurationStatus.Skipped, result.Status));
        Assert.Equal(disabled, await File.ReadAllTextAsync(local).DefaultTimeout());
        File.Delete(local);

        var managed = Path.Combine(context.Paths.ManagedDirectory(copilot: false), "managed-settings.json");
        const string policy = """{"strictKnownMarketplaces":[]}""";
        await AgentConfigurationTestContext.WriteAsync(managed, policy).DefaultTimeout();

        var blocked = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.All(blocked, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(policy, await File.ReadAllTextAsync(managed).DefaultTimeout());
        Assert.False(File.Exists(Path.Combine(context.Paths.ClaudeDirectory, "settings.json")));
    }

    [Fact]
    public async Task Mcp_UsesSharedCopilotClaudeProjectAndSeparateNativeUserFiles()
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request(
            [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp, AgentClientKind.ClaudeCode, AgentClientKind.VsCode],
            skills: false, mcp: true);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(5, results.Count);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var shared = results.Single(result => result.TargetPath == Path.Combine(context.Project.FullName, ".mcp.json"));
        Assert.Equal([AgentClientKind.CopilotCli, AgentClientKind.CopilotApp, AgentClientKind.ClaudeCode], shared.Clients);
        Assert.Equal(
            new[]
            {
                Path.Combine(context.Project.FullName, ".mcp.json"),
                Path.Combine(context.Paths.CopilotDirectory, "mcp-config.json"),
                context.Paths.ClaudeMcpFile,
                Path.Combine(context.Project.FullName, ".vscode", "mcp.json"),
                Path.Combine(context.Paths.VsCodeUserDirectory(false), "mcp.json")
            }.Order(AgentConfigurationPath.Comparer),
            results.Select(result => result.TargetPath).Order(AgentConfigurationPath.Comparer));
        Assert.Empty(context.SkillInstaller.Requests);
        await Verify(await File.ReadAllTextAsync(shared.TargetPath).DefaultTimeout(), "json");
    }

    [Fact]
    public async Task SharedMcpEntry_DoesNotBypassClaudesManagedPolicy()
    {
        using var context = new AgentConfigurationTestContext(output);
        var managed = Path.Combine(context.Paths.ManagedDirectory(copilot: false), "managed-mcp.json");
        const string policy = """{"mcpServers":{}}""";
        await AgentConfigurationTestContext.WriteAsync(managed, policy).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, results.Single(result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Configured, results.Single(result => result.TargetPath == Path.Combine(context.Paths.CopilotDirectory, "mcp-config.json")).Status);
        Assert.Equal(policy, await File.ReadAllTextAsync(managed).DefaultTimeout());
        Assert.False(File.Exists(Path.Combine(context.Project.FullName, ".mcp.json")));
    }

    [Fact]
    public async Task Mcp_RepairsOnlyTheDeprecatedPrefix()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Paths.CopilotDirectory, "mcp-config.json");
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

        var result = await context.ConfigureNativeAsync(context.Request([AgentClientKind.CopilotCli], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, result.Single(target => target.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal(AgentConfigurationStatus.Skipped, result.Single(target => target.Scope is AgentConfigurationScope.Project).Status);
        Assert.False(File.Exists(Path.Combine(context.Project.FullName, ".github", "mcp.json")));
        await Verify(await File.ReadAllTextAsync(path).DefaultTimeout(), "json");
    }

    [Fact]
    public async Task Claude_UsesDocumentedConfigDirectoryOverrideForSettingsAndUserMcp()
    {
        using var context = new AgentConfigurationTestContext(output);
        var custom = Path.Combine(context.Home.FullName, "claude-work");
        context.SetVariable("CLAUDE_CONFIG_DIR", custom);
        var state = Path.Combine(custom, ".claude.json");
        const string existing = """{"oauthAccount":{"accountUuid":"preserved"},"projects":{},"theme":"dark"}""";
        await AgentConfigurationTestContext.WriteAsync(state, existing).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.ClaudeCode], mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.True(File.Exists(Path.Combine(custom, "settings.json")));
        Assert.False(File.Exists(Path.Combine(context.Home.FullName, ".claude.json")));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(state).DefaultTimeout())!.AsObject();
        config.Remove("mcpServers");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(existing), config));
    }

    [Fact]
    public async Task VsCode_UsesDetectedInsidersAndExistingProfile()
    {
        using var context = new AgentConfigurationTestContext(output);
        var user = context.Paths.VsCodeUserDirectory(insiders: true);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(user, "profiles", "work-profile", "settings.json"), "{}").DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(user, "profiles", "builtin", "settings.json"), "{}").DefaultTimeout();
        var request = context.Request([AgentClientKind.VsCode], skills: false, mcp: true,
            detections: [new(AgentClientKind.VsCode, "1.110.0", IsInsiders: true)]);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.True(File.Exists(Path.Combine(user, "mcp.json")));
        Assert.True(File.Exists(Path.Combine(user, "profiles", "work-profile", "mcp.json")));
        Assert.False(Directory.Exists(context.Paths.VsCodeUserDirectory(insiders: false)));
    }

    [Theory]
    [InlineData("VSCODE_PORTABLE")]
    [InlineData("VSCODE_APPDATA")]
    public async Task VsCode_HonorsVerifiedUserDataOverrides(string variable)
    {
        using var context = new AgentConfigurationTestContext(output);
        var custom = Path.Combine(context.Home.FullName, "custom-code");
        context.SetVariable(variable, custom);

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.VsCode], skills: false, mcp: true)).DefaultTimeout();

        var userPath = Path.Combine(custom, variable == "VSCODE_PORTABLE" ? "user-data" : "Code", "User", "mcp.json");
        Assert.Equal(userPath, results.Single(result => result.Scope is AgentConfigurationScope.User).TargetPath);
        Assert.True(File.Exists(userPath));
    }

    [Fact]
    public async Task Copilot_CollapsesProjectAndUserAliases()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("COPILOT_HOME", Path.Combine(context.Project.FullName, ".github", "copilot"));
        var request = context.Request([AgentClientKind.CopilotCli, AgentClientKind.CopilotApp]);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();
        var repeated = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.Equal(AgentConfigurationScope.User, results[0].Scope);
        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(repeated).Status);
    }

    [Fact]
    public async Task Claude_ManagedOnlyAllowlistIsNotOverriddenByAUserAllowlist()
    {
        using var context = new AgentConfigurationTestContext(output);
        var managed = Path.Combine(context.Paths.ManagedDirectory(copilot: false), "managed-settings.json");
        await AgentConfigurationTestContext.WriteAsync(managed,
            """{"allowManagedMcpServersOnly":true,"allowedMcpServers":[{"serverName":"aspire"}]}""").DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Paths.ClaudeDirectory, "settings.json"),
            """{"allowedMcpServers":[]}""").DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.ClaudeCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
    }

    [Fact]
    public async Task VsCode_RelativeAppDataOverrideUsesOriginalVsCodeDirectory()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("VSCODE_APPDATA", "relative-data");
        context.SetVariable("VSCODE_CWD", context.Workspace.WorkspaceRoot.FullName);

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.VsCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(Path.Combine(context.Workspace.WorkspaceRoot.FullName, "relative-data", "Code", "User", "mcp.json"),
            results.Single(result => result.Scope is AgentConfigurationScope.User).TargetPath);
    }
}
