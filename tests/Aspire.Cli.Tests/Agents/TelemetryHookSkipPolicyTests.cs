// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Agents;

public class TelemetryHookSkipPolicyTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("COPILOT_CLI", "1")]
    [InlineData("AI_AGENT", "github_copilot_app_agent")]
    public void ClaudeHookDefersToCopilotHookInsideCopilot(string variable, string value)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        context.SetVariable(variable, value);
        var policy = CreatePolicy(context, context.Project);

        Assert.True(policy.ShouldSkip(ClaudeCodeAgentClient.ClientId, AgentTelemetryProtocol.UserScope));
        Assert.True(policy.ShouldSkip(ClaudeCodeAgentClient.ClientId, AgentTelemetryProtocol.ProjectScope));
        Assert.False(policy.ShouldSkip(CopilotAgentClient.ClientId, AgentTelemetryProtocol.UserScope));
        Assert.False(policy.ShouldSkip(source: null, scope: null));
    }

    [Theory]
    [InlineData(CopilotAgentClient.ClientId, ClaudeCodeAgentClient.ClientId)]
    [InlineData(ClaudeCodeAgentClient.ClientId, CopilotAgentClient.ClientId)]
    public async Task UserHookDefersToProjectHookForSameSource(string source, string otherSource)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        Directory.CreateDirectory(Path.Combine(context.Project.FullName, ".git"));
        var client = context.Clients.Single(client => client.Id == source);
        Assert.Equal(
            TelemetryHookStatus.Configured,
            await client.ConfigureTelemetryHookAsync(AgentConfigurationScope.Project, context.Project, CancellationToken.None));
        var policy = CreatePolicy(context, context.Project.CreateSubdirectory(Path.Combine("src", "app")));

        Assert.True(policy.ShouldSkip(source, AgentTelemetryProtocol.UserScope));
        Assert.False(policy.ShouldSkip(source, AgentTelemetryProtocol.ProjectScope));
        Assert.False(policy.ShouldSkip(otherSource, AgentTelemetryProtocol.UserScope));
        Assert.False(policy.ShouldSkip(source, scope: null));
    }

    [Theory]
    [InlineData(CopilotAgentClient.ClientId)]
    [InlineData(ClaudeCodeAgentClient.ClientId)]
    public void UserHookRecordsWhenRepositoryHasNoProjectHook(string source)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        Directory.CreateDirectory(Path.Combine(context.Project.FullName, ".git"));
        var policy = CreatePolicy(context, context.Project);

        Assert.False(policy.ShouldSkip(source, AgentTelemetryProtocol.UserScope));

        // A user-scope hook copied into the repository does not own the event.
        WriteProjectHook(context.Project, source, AgentTelemetryHook.GetHookCommand(source, AgentConfigurationScope.Global));
        Assert.False(policy.ShouldSkip(source, AgentTelemetryProtocol.UserScope));

        // Neither does a hook installed before --scope existed.
        WriteProjectHook(context.Project, source, AgentTelemetryHook.GetCommand(AgentTelemetryProtocol.HookOptionName));
        Assert.False(policy.ShouldSkip(source, AgentTelemetryProtocol.UserScope));

        // Unreadable project settings fail open.
        File.WriteAllText(GetProjectHookPath(context.Project, source), "{ malformed");
        Assert.False(policy.ShouldSkip(source, AgentTelemetryProtocol.UserScope));
    }

    private static TelemetryHookSkipPolicy CreatePolicy(AgentConfigurationTestContext context, DirectoryInfo workingDirectory)
        => new(context.Clients, TestExecutionContextHelper.CreateExecutionContext(workingDirectory, homeDirectory: context.Home), context.Environment);

    private static string GetProjectHookPath(DirectoryInfo root, string source)
        => source == CopilotAgentClient.ClientId
            ? Path.Combine(root.FullName, ".github", "hooks", CopilotAgentClient.TelemetryHookFileName)
            : Path.Combine(root.FullName, ".claude", "settings.json");

    // Writes the minimal shape each client reads; TelemetryHookConfiguratorTests covers the full files init writes.
    private static void WriteProjectHook(DirectoryInfo root, string source, (string Command, string[] Args) invocation)
    {
        var path = GetProjectHookPath(root, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var args = new JsonArray(invocation.Args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray());
        JsonNode settings = source == CopilotAgentClient.ClientId
            ? new JsonObject
            {
                ["version"] = 1,
                ["hooks"] = new JsonObject
                {
                    ["postToolUse"] = new JsonArray(new JsonObject { ["type"] = "command", ["exec"] = invocation.Command, ["args"] = args })
                }
            }
            : new JsonObject
            {
                ["hooks"] = new JsonObject
                {
                    ["PostToolUse"] = new JsonArray(new JsonObject
                    {
                        ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = invocation.Command, ["args"] = args })
                    })
                }
            };
        File.WriteAllText(path, settings.ToJsonString());
    }
}
