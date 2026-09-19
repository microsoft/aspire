// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Resources;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class TelemetryHookConfiguratorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task Plan_WritesCopilotUserHookAfterNativeRegistration()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var request = context.Request([AgentClientKind.CopilotCli]);

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        AssertNativeConfigured(result);
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(AgentConfigurationStatus.Configured, hook.Status);
        Assert.Equal([AgentClientKind.CopilotCli], hook.Clients);
        Assert.Equal(AgentConfigurationScope.User, hook.Scope);
        Assert.Equal(1, context.HookInstaller.Calls);

        var root = await ReadObjectAsync(hook.TargetPath).DefaultTimeout();
        Assert.Equal(1, (int)root["version"]!);
        var entry = Assert.Single(root["hooks"]!["postToolUse"]!.AsArray())!.AsObject();
        Assert.Equal("command", (string?)entry["type"]);
        Assert.Equal(30, (int)entry["timeoutSec"]!);
        Assert.Equal(HookCommandFormatter.BuildBashCommand(Path.Combine(context.ExecutionContext.AspireHomeDirectory.FullName, "hooks", "track-telemetry.sh")),
            (string?)entry["bash"]);
        Assert.Equal(HookCommandFormatter.BuildPwshCommand(Path.Combine(context.ExecutionContext.AspireHomeDirectory.FullName, "hooks", "track-telemetry.ps1")),
            (string?)entry["powershell"]);
    }

    [Fact]
    public async Task Plan_RegistersOneSharedCopilotHookForSelectedAppAndCli()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        AgentClientKind[] clients = [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp];

        var result = await context.Service.ConfigureAsync(context.Request(clients), CancellationToken.None).DefaultTimeout();

        AssertNativeConfigured(result);
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(clients, hook.Clients);
        Assert.Equal(AgentConfigurationStatus.Configured, hook.Status);
        Assert.Equal(1, context.HookInstaller.Calls);
    }

    [Fact]
    public async Task Plan_HonorsCopilotHomeForBothRegistrationAndHook()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var custom = context.Workspace.CreateDirectory("custom-copilot");
        context.SetVariable("COPILOT_HOME", custom.FullName);

        var result = await context.Service.ConfigureAsync(context.Request([AgentClientKind.CopilotCli]), CancellationToken.None).DefaultTimeout();

        AssertNativeConfigured(result);
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(Path.Combine(custom.FullName, "hooks", "aspire-telemetry.json"), hook.TargetPath);
        Assert.True(File.Exists(Path.Combine(custom.FullName, "settings.json")));
        Assert.True(File.Exists(hook.TargetPath));
        Assert.False(Directory.Exists(Path.Combine(context.Home.FullName, ".copilot")));
    }

    [Fact]
    public async Task Plan_WritesClaudeUserHookWithExistingExecFormAndTimeout()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);

        var result = await context.Service.ConfigureAsync(context.Request([AgentClientKind.ClaudeCode]), CancellationToken.None).DefaultTimeout();

        AssertNativeConfigured(result);
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(AgentConfigurationStatus.Configured, hook.Status);
        var root = await ReadObjectAsync(hook.TargetPath).DefaultTimeout();
        var group = Assert.Single(root["hooks"]!["PostToolUse"]!.AsArray())!.AsObject();
        Assert.Equal("*", (string?)group["matcher"]);
        var entry = Assert.Single(group["hooks"]!.AsArray())!.AsObject();
        Assert.Equal("command", (string?)entry["type"]);
        Assert.Equal(30, (int)entry["timeout"]!);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("pwsh", (string?)entry["command"]);
            Assert.Equal(
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(context.ExecutionContext.AspireHomeDirectory.FullName, "hooks", "track-telemetry.ps1")],
                entry["args"]!.AsArray().Select(value => (string)value!));
        }
        else
        {
            Assert.Equal("bash", (string?)entry["command"]);
            Assert.Equal([Path.Combine(context.ExecutionContext.AspireHomeDirectory.FullName, "hooks", "track-telemetry.sh")],
                entry["args"]!.AsArray().Select(value => (string)value!));
        }
    }

    [Fact]
    public async Task Plan_PreservesExistingClaudeHooksAndSettings()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var path = Path.Combine(context.Paths.ClaudeDirectory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, """
            {
              "model": "preserved",
              "hooks": {
                "PostToolUse": [{
                  "matcher": "Write",
                  "hooks": [{ "type": "command", "command": "echo existing" }]
                }]
              }
            }
            """).DefaultTimeout();

        var result = await context.Service.ConfigureAsync(context.Request([AgentClientKind.ClaudeCode]), CancellationToken.None).DefaultTimeout();

        AssertNativeConfigured(result);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).Status);
        var root = await ReadObjectAsync(path).DefaultTimeout();
        Assert.Equal("preserved", (string?)root["model"]);
        var groups = root["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("Write", (string?)groups[0]!["matcher"]);
        Assert.Equal("echo existing", (string?)groups[0]!["hooks"]![0]!["command"]);
        Assert.Equal("*", (string?)groups[1]!["matcher"]);
    }

    [Theory]
    [InlineData("{ this is not valid json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"hooks":"not-an-object"}""")]
    public async Task Plan_BlocksMalformedCopilotHookFileWithoutUndoingNativeRegistration(string existing)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var path = Path.Combine(context.Paths.CopilotDirectory, "hooks", "aspire-telemetry.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var result = await context.Service.ConfigureAsync(context.Request([AgentClientKind.CopilotCli]), CancellationToken.None).DefaultTimeout();

        AssertNativeConfigured(result);
        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).Status);
        Assert.False(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Theory]
    [InlineData("""{"hooks":"not-an-object"}""")]
    [InlineData("""{"hooks":{"PostToolUse":{}}}""")]
    [InlineData("""{"hooks":{"PostToolUse":[{"hooks":{}}]}}""")]
    public async Task Plan_BlocksUnexpectedClaudeHookShapeAfterUnchangedNativeRegistration(string hookSettings)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var request = context.Request([AgentClientKind.ClaudeCode]);
        await context.ConfigureNativeAsync(request).DefaultTimeout();
        var path = Path.Combine(context.Paths.ClaudeDirectory, "settings.json");
        var root = await ReadObjectAsync(path).DefaultTimeout();
        root["hooks"] = JsonNode.Parse(hookSettings)!["hooks"]!.DeepClone();
        var existing = root.ToJsonString();
        await File.WriteAllTextAsync(path, existing).DefaultTimeout();

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal(AgentConfigurationStatus.Unchanged, target.Status));
        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).Status);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Fact]
    public async Task Plan_WithoutSuccessfulCoreEvidence_DoesNotMaterializeHooks()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var request = context.Request([AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode]);

        var results = await context.Writer.ApplyAsync(context.Hooks.Plan(request), CancellationToken.None).DefaultTimeout();

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(AgentConfigurationStatus.Skipped, result.Status);
            Assert.Equal(AgentConfigurationStrings.HookNotApplicable, result.Message);
        });
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task Plan_CoreSuccessForAnotherClientDoesNotSatisfyEligibility()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var coreRequest = context.Request([AgentClientKind.ClaudeCode]);
        var hookRequest = context.Request([AgentClientKind.CopilotCli]);

        var results = await context.Writer.ApplyAsync(
            context.Planner.GetTargets(coreRequest).Concat(context.Hooks.Plan(hookRequest)), CancellationToken.None).DefaultTimeout();

        Assert.All(results.Where(result => result.Asset is AgentAssetKind.AspireSkills),
            result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal(AgentConfigurationStatus.Skipped, Assert.Single(results, result => result.Asset is AgentAssetKind.TelemetryHooks).Status);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.False(Directory.Exists(context.Paths.CopilotDirectory));
    }

    [Fact]
    public void Plan_OnlyCreatesTargetsForSupportedClientsAndNativeAssets()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);

        Assert.Empty(context.Hooks.Plan(context.Request([AgentClientKind.VsCode, AgentClientKind.OpenCode])));
        Assert.Empty(context.Hooks.Plan(context.Request([AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode],
            skills: false, playwright: true, dotnetInspect: true)));
        Assert.Empty(context.Hooks.Plan(context.Request([])));
        Assert.Equal(0, context.HookInstaller.Calls);
    }

    [Theory]
    [InlineData(nameof(AgentClientKind.CopilotCli))]
    [InlineData(nameof(AgentClientKind.ClaudeCode))]
    public async Task Plan_PreservesBytesAndTimestampOnRepeat(string clientName)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var request = context.Request([Enum.Parse<AgentClientKind>(clientName)]);
        var first = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();
        var path = Assert.Single(first.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).TargetPath;
        var bytes = await File.ReadAllBytesAsync(path).DefaultTimeout();
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.All(result.Targets, target => Assert.Equal(AgentConfigurationStatus.Unchanged, target.Status));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Plan_HonorsClaudeConfigDirectoryAndJsonc()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        context.SetVariable("CLAUDE_CONFIG_DIR", @"~\claude-work");
        var path = Path.Combine(context.Home.FullName, "claude-work", "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, """{/* comment */"model":"preserved",}""").DefaultTimeout();

        var result = await context.Service.ConfigureAsync(context.Request([AgentClientKind.ClaudeCode]), CancellationToken.None).DefaultTimeout();

        AssertNativeConfigured(result);
        Assert.Equal(path, Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).TargetPath);
        var root = await ReadObjectAsync(path).DefaultTimeout();
        Assert.Equal("preserved", (string?)root["model"]);
        Assert.Single(root["hooks"]!["PostToolUse"]!.AsArray());
        Assert.False(Directory.Exists(Path.Combine(context.Home.FullName, ".claude")));
        Assert.False(Directory.Exists(Path.Combine(context.Project.FullName, "~")));
    }

    [Fact]
    public async Task Plan_PreservesThirdPartyHooksWithTheSameScriptName()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var path = Path.Combine(context.Paths.ClaudeDirectory, "settings.json");
        const string command = "bash /vendor/other-product/track-telemetry.sh";
        await AgentConfigurationTestContext.WriteAsync(path, """
            {
              "hooks": {
                "PostToolUse": [{
                  "matcher": "*",
                  "hooks": [{ "type": "command", "command": "bash /vendor/other-product/track-telemetry.sh" }]
                }]
              }
            }
            """).DefaultTimeout();

        var result = await context.Service.ConfigureAsync(context.Request([AgentClientKind.ClaudeCode]), CancellationToken.None).DefaultTimeout();

        AssertNativeConfigured(result);
        var root = await ReadObjectAsync(path).DefaultTimeout();
        var groups = root["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal(command, (string?)groups[0]!["hooks"]![0]!["command"]);
        Assert.Equal(OperatingSystem.IsWindows() ? "pwsh" : "bash", (string?)groups[1]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public async Task Plan_ExistingProjectHookPreventsDuplicateUserRegistration()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var request = context.Request([AgentClientKind.ClaudeCode]);
        await context.ConfigureNativeAsync(request).DefaultTimeout();
        var projectPath = Path.Combine(context.Project.FullName, ".claude", "settings.json");
        var root = await ReadObjectAsync(projectPath).DefaultTimeout();
        root["hooks"] = new JsonObject
        {
            ["PostToolUse"] = new JsonArray(new JsonObject
            {
                ["matcher"] = "*",
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = "bash",
                    ["args"] = new JsonArray(Path.Combine(context.ExecutionContext.AspireHomeDirectory.FullName, "hooks", "track-telemetry.sh"))
                })
            })
        };
        var existing = root.ToJsonString();
        await File.WriteAllTextAsync(projectPath, existing).DefaultTimeout();

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal(AgentConfigurationStatus.Unchanged, target.Status));
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(AgentConfigurationStatus.Skipped, hook.Status);
        Assert.Equal(AgentConfigurationStrings.ExistingProjectHook, hook.Message);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.Equal(existing, await File.ReadAllTextAsync(projectPath).DefaultTimeout());
        Assert.Null((await ReadObjectAsync(hook.TargetPath).DefaultTimeout())["hooks"]);
    }

    [Fact]
    public async Task Plan_RespectsExplicitHookDisablement()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var request = context.Request([AgentClientKind.ClaudeCode]);
        await context.ConfigureNativeAsync(request).DefaultTimeout();
        var path = Path.Combine(context.Paths.ClaudeDirectory, "settings.json");
        var root = await ReadObjectAsync(path).DefaultTimeout();
        root["disableAllHooks"] = true;
        var existing = root.ToJsonString();
        await File.WriteAllTextAsync(path, existing).DefaultTimeout();

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal(AgentConfigurationStatus.Unchanged, target.Status));
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(AgentConfigurationStatus.Skipped, hook.Status);
        Assert.Equal(AgentConfigurationStrings.PolicyBlocked, hook.Message);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Equal(0, context.HookInstaller.Calls);
    }

    private static void AssertNativeConfigured(AgentInitResult result)
    {
        Assert.False(result.HasErrors);
        var native = result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills).ToArray();
        Assert.NotEmpty(native);
        Assert.All(native, target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
    }

    private static async Task<JsonObject> ReadObjectAsync(string path)
        => JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
}
