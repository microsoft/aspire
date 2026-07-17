// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class TelemetryHookConfiguratorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ConfigureAsync_WritesCopilotUserHook_WithExpectedShape()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var configurator = CreateConfigurator(workspace, home);

        var result = await configurator.ConfigureAsync([AgentClientKind.CopilotCli], CancellationToken.None).DefaultTimeout();

        Assert.Contains(AgentClientKind.CopilotCli, result.ConfiguredClients);
        Assert.Empty(result.Skipped);

        var hookFile = Path.Combine(home.FullName, ".copilot", "hooks", "aspire-telemetry.json");
        Assert.True(File.Exists(hookFile));

        var root = JsonNode.Parse(await File.ReadAllTextAsync(hookFile).DefaultTimeout())!.AsObject();
        Assert.Equal(1, (int)root["version"]!);

        var entry = root["hooks"]!["postToolUse"]!.AsArray()[0]!.AsObject();
        Assert.Equal("command", (string)entry["type"]!);
        Assert.Equal(30, (int)entry["timeoutSec"]!);
        Assert.Contains("track-telemetry.sh", (string)entry["bash"]!);
        Assert.StartsWith("bash ", (string)entry["bash"]!);
        Assert.Contains("track-telemetry.ps1", (string)entry["powershell"]!);
        Assert.Contains("-File ", (string)entry["powershell"]!);
        // Copilot CLI requires PowerShell 7+ on Windows, so the hook must invoke pwsh, not Windows PowerShell.
        Assert.StartsWith("pwsh ", (string)entry["powershell"]!);
    }

    [Fact]
    public async Task ConfigureAsync_HonorsCopilotHomeEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var copilotHome = workspace.CreateDirectory("custom-copilot");
        var configurator = CreateConfigurator(workspace, home, new Dictionary<string, string?>
        {
            ["COPILOT_HOME"] = copilotHome.FullName,
        });

        await configurator.ConfigureAsync([AgentClientKind.CopilotCli], CancellationToken.None).DefaultTimeout();

        Assert.True(File.Exists(Path.Combine(copilotHome.FullName, "hooks", "aspire-telemetry.json")));
        Assert.False(Directory.Exists(Path.Combine(home.FullName, ".copilot")));
    }

    [Fact]
    public async Task ConfigureAsync_WritesClaudeUserHook_WithTimeout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var configurator = CreateConfigurator(workspace, home);

        var result = await configurator.ConfigureAsync([AgentClientKind.ClaudeCode], CancellationToken.None).DefaultTimeout();

        Assert.Contains(AgentClientKind.ClaudeCode, result.ConfiguredClients);
        Assert.Empty(result.Skipped);

        var postToolUse = await ReadClaudePostToolUseAsync(home).DefaultTimeout();
        var ourGroups = CountAspireGroups(postToolUse);
        Assert.Equal(1, ourGroups);

        var entry = FindAspireHook(postToolUse);
        Assert.Equal("command", (string)entry["type"]!);
        Assert.Equal(30, (int)entry["timeout"]!);

        // Claude uses exec form (command + args): the executable is spawned directly and the script path is
        // a discrete argument, not part of a shell command string.
        var execCommand = (string)entry["command"]!;
        var args = entry["args"]!.AsArray().Select(a => (string)a!).ToArray();
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("pwsh", execCommand);
            Assert.Contains("-File", args);
            Assert.Contains(args, a => a.EndsWith("track-telemetry.ps1", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Equal("bash", execCommand);
            Assert.Contains(args, a => a.EndsWith("track-telemetry.sh", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ConfigureAsync_IsIdempotent_ForClaude()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var configurator = CreateConfigurator(workspace, home);

        await configurator.ConfigureAsync([AgentClientKind.ClaudeCode], CancellationToken.None).DefaultTimeout();
        await configurator.ConfigureAsync([AgentClientKind.ClaudeCode], CancellationToken.None).DefaultTimeout();

        var postToolUse = await ReadClaudePostToolUseAsync(home).DefaultTimeout();
        Assert.Equal(1, CountAspireGroups(postToolUse));
    }

    [Fact]
    public async Task ConfigureAsync_PreservesExistingClaudeConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var claudeDirectory = Directory.CreateDirectory(Path.Combine(home.FullName, ".claude"));
        var settingsPath = Path.Combine(claudeDirectory.FullName, "settings.json");

        var existing = new JsonObject
        {
            ["model"] = "claude-opus",
            ["hooks"] = new JsonObject
            {
                ["PostToolUse"] = new JsonArray(
                    new JsonObject
                    {
                        ["matcher"] = "Write",
                        ["hooks"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = "echo existing",
                            }),
                    }),
            },
        };
        await File.WriteAllTextAsync(settingsPath, existing.ToJsonString()).DefaultTimeout();

        var configurator = CreateConfigurator(workspace, home);
        await configurator.ConfigureAsync([AgentClientKind.ClaudeCode], CancellationToken.None).DefaultTimeout();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath).DefaultTimeout())!.AsObject();
        Assert.Equal("claude-opus", (string)root["model"]!);

        var postToolUse = root["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Contains(postToolUse, group => GroupContainsCommand(group, "echo existing"));
        Assert.Equal(1, CountAspireGroups(postToolUse));
    }

    [Fact]
    public async Task ConfigureAsync_SkipsClaude_WhenSettingsAreMalformed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var claudeDirectory = Directory.CreateDirectory(Path.Combine(home.FullName, ".claude"));
        var settingsPath = Path.Combine(claudeDirectory.FullName, "settings.json");
        const string malformed = "{ this is not valid json";
        await File.WriteAllTextAsync(settingsPath, malformed).DefaultTimeout();

        var configurator = CreateConfigurator(workspace, home);
        var result = await configurator.ConfigureAsync([AgentClientKind.ClaudeCode], CancellationToken.None).DefaultTimeout();

        Assert.DoesNotContain(AgentClientKind.ClaudeCode, result.ConfiguredClients);
        Assert.Contains(result.Skipped, s => s.Client == AgentClientKind.ClaudeCode && s.Reason == TelemetryHookSkipReason.MalformedConfig);
        // The malformed file must be left untouched, never clobbered.
        Assert.Equal(malformed, await File.ReadAllTextAsync(settingsPath).DefaultTimeout());
    }

    [Fact]
    public async Task ConfigureAsync_SkipsClaude_WhenHooksShapeIsUnexpected()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var claudeDirectory = Directory.CreateDirectory(Path.Combine(home.FullName, ".claude"));
        var settingsPath = Path.Combine(claudeDirectory.FullName, "settings.json");
        const string unexpected = "{\"hooks\":\"not-an-object\"}";
        await File.WriteAllTextAsync(settingsPath, unexpected).DefaultTimeout();

        var configurator = CreateConfigurator(workspace, home);
        var result = await configurator.ConfigureAsync([AgentClientKind.ClaudeCode], CancellationToken.None).DefaultTimeout();

        Assert.Contains(result.Skipped, s => s.Client == AgentClientKind.ClaudeCode && s.Reason == TelemetryHookSkipReason.UnexpectedConfigShape);
        Assert.Equal(unexpected, await File.ReadAllTextAsync(settingsPath).DefaultTimeout());
    }

    [Fact]
    public async Task ConfigureAsync_SkipsClaude_WhenSettingsRootIsNotAnObject()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var claudeDirectory = Directory.CreateDirectory(Path.Combine(home.FullName, ".claude"));
        var settingsPath = Path.Combine(claudeDirectory.FullName, "settings.json");
        // Valid JSON, but the root is an array rather than an object. JsonNode.AsObject() throws
        // InvalidOperationException on this input, so the configurator must skip it like any other
        // unrecognized shape instead of letting that exception crash `agent init`.
        const string nonObjectRoot = "[1, 2, 3]";
        await File.WriteAllTextAsync(settingsPath, nonObjectRoot).DefaultTimeout();

        var configurator = CreateConfigurator(workspace, home);
        var result = await configurator.ConfigureAsync([AgentClientKind.ClaudeCode], CancellationToken.None).DefaultTimeout();

        Assert.Contains(result.Skipped, s => s.Client == AgentClientKind.ClaudeCode && s.Reason == TelemetryHookSkipReason.UnexpectedConfigShape);
        Assert.Equal(nonObjectRoot, await File.ReadAllTextAsync(settingsPath).DefaultTimeout());
    }

    [Fact]
    public async Task ConfigureAsync_IsNoOp_ForUnsupportedClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var configurator = CreateConfigurator(workspace, home);

        var result = await configurator.ConfigureAsync(
            [AgentClientKind.VsCode, AgentClientKind.OpenCode],
            CancellationToken.None).DefaultTimeout();

        Assert.Empty(result.ConfiguredClients);
        Assert.Empty(result.Skipped);
        // Nothing is materialized when no supported client is present.
        Assert.False(Directory.Exists(Path.Combine(home.FullName, ".aspire", "hooks")));
        Assert.False(Directory.Exists(Path.Combine(home.FullName, ".copilot")));
        Assert.False(Directory.Exists(Path.Combine(home.FullName, ".claude")));
    }

    private static async Task<JsonArray> ReadClaudePostToolUseAsync(DirectoryInfo home)
    {
        var settingsPath = Path.Combine(home.FullName, ".claude", "settings.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        return root["hooks"]!["PostToolUse"]!.AsArray();
    }

    private static int CountAspireGroups(JsonArray postToolUse)
        => postToolUse.Count(GroupContainsAspireHook);

    private static bool GroupContainsAspireHook(JsonNode? group)
        => group is JsonObject obj
            && obj["hooks"] is JsonArray hooks
            && hooks.Any(HookReferencesTelemetryScript);

    // The Aspire hook can carry the script path in the shell-form `command` string or, for Claude's exec
    // form, in an `args` element. Check both so helpers locate the entry regardless of format.
    private static bool HookReferencesTelemetryScript(JsonNode? hook)
        => hook is JsonObject ho
            && (JsonValueHasTelemetryScript(ho["command"])
                || (ho["args"] is JsonArray args && args.Any(JsonValueHasTelemetryScript)));

    private static bool JsonValueHasTelemetryScript(JsonNode? node)
        => node is JsonValue v && v.ToString().Contains("track-telemetry", StringComparison.OrdinalIgnoreCase);

    private static bool GroupContainsCommand(JsonNode? group, string command)
        => group is JsonObject obj
            && obj["hooks"] is JsonArray hooks
            && hooks.Any(h => h is JsonObject ho
                && ho["command"] is JsonValue v
                && v.ToString() == command);

    private static JsonObject FindAspireHook(JsonArray postToolUse)
    {
        foreach (var group in postToolUse)
        {
            if (group is JsonObject obj && obj["hooks"] is JsonArray hooks)
            {
                foreach (var hook in hooks)
                {
                    if (hook is JsonObject ho && HookReferencesTelemetryScript(ho))
                    {
                        return ho;
                    }
                }
            }
        }

        throw new InvalidOperationException("No Aspire hook entry was found.");
    }

    private static TelemetryHookConfigurator CreateConfigurator(
        TemporaryWorkspace workspace,
        DirectoryInfo home,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            workspace.WorkspaceRoot,
            homeDirectory: home);
        var environment = new TestEnvironment(environmentVariables);
        var installer = new TelemetryHookInstaller(executionContext, NullLogger<TelemetryHookInstaller>.Instance);
        return new TelemetryHookConfigurator(installer, executionContext, environment, NullLogger<TelemetryHookConfigurator>.Instance);
    }
}
