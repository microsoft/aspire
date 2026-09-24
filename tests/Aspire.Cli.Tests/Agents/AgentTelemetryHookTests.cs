// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Tests.Utils;

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
        Assert.Null(AgentTelemetryHook.Classify(payload, null, AgentTelemetryHook.DefaultMaxPayloadCharacters));
    }

    [Theory]
    [InlineData("toolName", "toolArgs", "skill", """{"skill":"aspire"}""")]
    [InlineData("tool_name", "tool_input", "Skill", """{"skill":"aspire:aspire"}""")]
    public void SkillEventsPreserveArguments(string nameProperty, string inputProperty, string tool, string input)
    {
        var args = AgentTelemetryHook.Classify($$"""{"{{nameProperty}}":"{{tool}}","{{inputProperty}}":{{input}}}""", "1", AgentTelemetryHook.DefaultMaxPayloadCharacters);
        Assert.NotNull(args);
        Assert.Equal("aspire", args[Array.IndexOf(args, "--skill-name") + 1]);
        Assert.Equal("skill_invocation", args[Array.IndexOf(args, "--event-type") + 1]);
    }

    [Fact]
    public void OversizedPayloadIsIgnored()
    {
        const string payload = """{"toolName":"skill","toolArgs":{"skill":"aspire"}}""";
        Assert.NotNull(AgentTelemetryHook.Classify(payload, null, payload.Length));
        Assert.Null(AgentTelemetryHook.Classify(payload, null, payload.Length - 1));
    }

    [Fact]
    public async Task HookAlwaysReturnsOneBenignResponseWithoutStartingTelemetryForUnrelatedInput()
    {
        var hook = new AgentTelemetryHook(new TestEnvironment());
        using var input = new StringReader("""{"toolName":"bash","toolArgs":{"command":"echo hello"}}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = 0;
        var result = await hook.RunAsync(input, output, error, _ =>
        {
            calls++;
            return Task.FromResult(0);
        });
        Assert.Equal(0, result);
        Assert.Equal(0, calls);
        Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task HookOptOutDoesNotReadOrInvoke()
    {
        var hook = new AgentTelemetryHook(new TestEnvironment(new Dictionary<string, string?>
        {
            ["ASPIRE_CLI_TELEMETRY_OPTOUT"] = "TrUe"
        }));
        using var input = new StringReader("""{"toolName":"skill","toolArgs":{"skill":"aspire"}}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await hook.RunAsync(input, output, error, _ => throw new InvalidOperationException());
        Assert.Equal(0, result);
        Assert.Equal('{', input.Peek());
        Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void MalformedMcpArgumentsDoNotLoseToolInvocation()
    {
        var args = AgentTelemetryHook.Classify("""{"toolName":"aspire-list_resources","toolArgs":"not json"}""", "1", AgentTelemetryHook.DefaultMaxPayloadCharacters);
        Assert.NotNull(args);
        Assert.Equal("aspire-list_resources", args[Array.IndexOf(args, "--tool-name") + 1]);
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555", true)]
    [InlineData("11111111222233334444555555555555", false)]
    [InlineData("not-a-session-id", false)]
    public void SessionIdUsesGuidFormatValidation(string sessionId, bool expected)
    {
        var args = AgentTelemetryHook.Classify(
            $$"""{"toolName":"aspire-list_resources","sessionId":"{{sessionId}}"}""",
            "1", AgentTelemetryHook.DefaultMaxPayloadCharacters);
        Assert.NotNull(args);
        var index = Array.IndexOf(args, "--session-id");
        Assert.Equal(expected, index >= 0);
        if (expected)
        {
            Assert.Equal(sessionId, args[index + 1]);
        }
    }

    [Fact]
    public async Task HookUsesInjectedClientEnvironmentAndReturnsBenignResponseOnFailure()
    {
        var hook = new AgentTelemetryHook(new TestEnvironment(new Dictionary<string, string?>
        {
            ["COPILOT_CLI"] = "1"
        }));
        using var input = new StringReader("""{"hook_event_name":"PostToolUse","tool_name":"mcp__aspire__list_resources"}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[]? recordedArgs = null;

        Assert.Equal(0, await hook.RunAsync(input, output, error, args =>
        {
            recordedArgs = args;
            throw new IOException("Must not expose payload or exception message.");
        }));

        Assert.NotNull(recordedArgs);
        Assert.Equal("copilot-cli", recordedArgs[Array.IndexOf(recordedArgs, "--client-name") + 1]);
        Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        Assert.Equal("Agent telemetry hook failed (IOException)." + Environment.NewLine, error.ToString());
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
        Assert.Equal(Values(script, "AspireSkills"), AgentTelemetryCatalog.Bundled.Skills.Order());
        Assert.Equal(Values(script, "AspireMcpTools"), AgentTelemetryCatalog.Bundled.Tools.Order());
        Assert.Equal(Values(script, "AspireReferenceFiles"), AgentTelemetryCatalog.Bundled.References.Order());
    }

    [Theory]
    [InlineData(null, AgentTelemetryHook.DefaultMaxPayloadCharacters)]
    [InlineData("1", 1)]
    [InlineData("131072", 128 * 1024)]
    [InlineData("1048576", AgentTelemetryHook.MaximumPayloadCharacters)]
    public void PayloadLimitAcceptsValidConfiguration(string? value, int expected)
    {
        Assert.Equal(expected, AgentTelemetryHook.GetMaxPayloadCharacters(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1048577")]
    [InlineData("2147483648")]
    [InlineData("not-a-number")]
    public void PayloadLimitRejectsInvalidConfiguration(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() => AgentTelemetryHook.GetMaxPayloadCharacters(value));
        Assert.Contains(AgentTelemetryHook.PayloadLimitEnvironmentVariable, exception.Message);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task HookHonorsConfiguredLimitAndDrainsOversizedInput(int extraCharacters, bool expectedInvocation)
    {
        const string payload = """{"toolName":"skill","toolArgs":{"skill":"aspire"}}""";
        var hook = new AgentTelemetryHook(new TestEnvironment(new Dictionary<string, string?>
        {
            [AgentTelemetryHook.PayloadLimitEnvironmentVariable] = payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));
        using var input = new StringReader(payload + new string(' ', extraCharacters));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var invoked = false;

        Assert.Equal(0, await hook.RunAsync(input, output, error, _ =>
        {
            invoked = true;
            return Task.FromResult(0);
        }));

        Assert.Equal(expectedInvocation, invoked);
        Assert.Equal(-1, input.Peek());
        Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task HookReportsInvalidConfigurationWithoutInvokingCli()
    {
        var hook = new AgentTelemetryHook(new TestEnvironment(new Dictionary<string, string?>
        {
            [AgentTelemetryHook.PayloadLimitEnvironmentVariable] = "0"
        }));
        using var input = new StringReader("""{"toolName":"skill","toolArgs":{"skill":"aspire"}}""");
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, await hook.RunAsync(input, output, error, _ => throw new InvalidOperationException()));
        Assert.Contains(AgentTelemetryHook.PayloadLimitEnvironmentVariable, error.ToString());
        Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void ConfiguredLimitCanExceedLegacyDefault()
    {
        const string payload = """{"toolName":"skill","toolArgs":{"skill":"aspire"}}""";
        var padded = payload.PadRight(AgentTelemetryHook.DefaultMaxPayloadCharacters + 1);
        Assert.Null(AgentTelemetryHook.Classify(padded, null, AgentTelemetryHook.DefaultMaxPayloadCharacters));
        Assert.NotNull(AgentTelemetryHook.Classify(padded, null, padded.Length));
    }
}
