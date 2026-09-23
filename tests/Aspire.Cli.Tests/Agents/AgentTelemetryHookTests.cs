// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Cli.Agents.Hooks;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests.Agents;

public class AgentTelemetryHookTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{ malformed")]
    [InlineData("""{"toolName":"bash","toolArgs":{"command":"aspire run"}}""")]
    [InlineData("""{"toolName":"Read","tool_input":{"file_path":"/skills/third-party/SKILL.md"}}""")]
    public void UnrelatedOrMalformedEventsDoNotInitializeCli(string payload)
    {
        Assert.Null(AgentTelemetryHook.Classify(payload, null));
    }

    [Theory]
    [InlineData("toolName", "toolArgs", "skill", """{"skill":"aspire"}""")]
    [InlineData("tool_name", "tool_input", "Skill", """{"skill":"aspire:aspire"}""")]
    public void SkillEventsPreserveArguments(string nameProperty, string inputProperty, string tool, string input)
    {
        var args = AgentTelemetryHook.Classify($$"""{"{{nameProperty}}":"{{tool}}","{{inputProperty}}":{{input}}}""", "1");
        Assert.NotNull(args);
        Assert.Equal("aspire", args[Array.IndexOf(args, "--skill-name") + 1]);
        Assert.Equal("skill_invocation", args[Array.IndexOf(args, "--event-type") + 1]);
    }

    [Fact]
    public void OversizedPayloadIsIgnored()
    {
        Assert.Null(AgentTelemetryHook.Classify(new string(' ', 65537), null));
    }

    [Fact]
    public void HookAlwaysReturnsOneBenignResponseWithoutStartingCliForUnrelatedInput()
    {
        using var process = RemoteExecutor.Invoke(static async () =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_CLI_TELEMETRY_OPTOUT", "false");
            using var input = new StringReader("""{"toolName":"bash","toolArgs":{"command":"echo hello"}}""");
            using var output = new StringWriter();
            var calls = 0;
            var result = await AgentTelemetryHook.RunAsync(input, output, _ =>
            {
                calls++;
                return Task.FromResult(0);
            });
            Assert.Equal(0, result);
            Assert.Equal(0, calls);
            Assert.Equal("{\"continue\":true}" + Environment.NewLine, output.ToString());
        });
    }

    [Fact]
    public void HookOptOutDoesNotReadOrInvoke()
    {
        using var process = RemoteExecutor.Invoke(static async () =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_CLI_TELEMETRY_OPTOUT", "TrUe");
            using var input = new StringReader("""{"toolName":"skill","toolArgs":{"skill":"aspire"}}""");
            using var output = new StringWriter();
            var result = await AgentTelemetryHook.RunAsync(input, output, _ => throw new InvalidOperationException());
            Assert.Equal(0, result);
            Assert.Equal('{', input.Peek());
            Assert.Equal("{\"continue\":true}" + Environment.NewLine, output.ToString());
        });
    }

    [Fact]
    public void MalformedMcpArgumentsDoNotLoseToolInvocation()
    {
        var args = AgentTelemetryHook.Classify("""{"toolName":"aspire-list_resources","toolArgs":"not json"}""", "1");
        Assert.NotNull(args);
        Assert.Equal("aspire-list_resources", args[Array.IndexOf(args, "--tool-name") + 1]);
    }

    [Fact]
    public void AllowlistsMatchCanonicalPowerShellHook()
    {
        using var stream = typeof(AgentTelemetryHook).Assembly.GetManifestResourceStream("track-telemetry.ps1")!;
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        // Canonical declarations have the form $AspireSkills = @('aspire', 'aspire-init', ...).
        static string[] Values(string script, string name)
        {
            var body = Regex.Match(script, $@"\${name} = @\((.*?)\)", RegexOptions.Singleline).Groups[1].Value;
            return Regex.Matches(body, "'([^']+)'").Select(match => match.Groups[1].Value).Order().ToArray();
        }
        Assert.Equal(Values(script, "AspireSkills"), AgentTelemetryHook.s_skills.Order());
        Assert.Equal(Values(script, "AspireMcpTools"), AgentTelemetryHook.s_tools.Order());
        Assert.Equal(Values(script, "AspireReferenceFiles"), AgentTelemetryHook.s_references.Order());
    }
}
