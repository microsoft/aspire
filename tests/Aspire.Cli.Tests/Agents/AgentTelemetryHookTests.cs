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
    public void HookAlwaysReturnsOneBenignResponseWithoutStartingCliForUnrelatedInput()
    {
        using var process = RemoteExecutor.Invoke(static async () =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_CLI_TELEMETRY_OPTOUT", "false");
            Environment.SetEnvironmentVariable(AgentTelemetryHook.PayloadLimitEnvironmentVariable, null);
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
            Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
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
            Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        });
    }

    [Fact]
    public void MalformedMcpArgumentsDoNotLoseToolInvocation()
    {
        var args = AgentTelemetryHook.Classify("""{"toolName":"aspire-list_resources","toolArgs":"not json"}""", "1", AgentTelemetryHook.DefaultMaxPayloadCharacters);
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
    public void HookHonorsConfiguredLimitAndDrainsOversizedInput(int extraCharacters, bool expectedInvocation)
    {
        using var process = RemoteExecutor.Invoke(static async (extra, expected) =>
        {
            const string payload = """{"toolName":"skill","toolArgs":{"skill":"aspire"}}""";
            Environment.SetEnvironmentVariable("ASPIRE_CLI_TELEMETRY_OPTOUT", "false");
            Environment.SetEnvironmentVariable(AgentTelemetryHook.PayloadLimitEnvironmentVariable, payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var input = new StringReader(payload + new string(' ', int.Parse(extra)));
            using var output = new StringWriter();
            var invoked = false;

            Assert.Equal(0, await AgentTelemetryHook.RunAsync(input, output, _ =>
            {
                invoked = true;
                return Task.FromResult(0);
            }));

            Assert.Equal(bool.Parse(expected), invoked);
            Assert.Equal(-1, input.Peek());
            Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        }, extraCharacters.ToString(), expectedInvocation.ToString());
    }

    [Fact]
    public void HookReportsInvalidConfigurationWithoutInvokingCli()
    {
        using var process = RemoteExecutor.Invoke(static async () =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_CLI_TELEMETRY_OPTOUT", "false");
            Environment.SetEnvironmentVariable(AgentTelemetryHook.PayloadLimitEnvironmentVariable, "0");
            using var input = new StringReader("""{"toolName":"skill","toolArgs":{"skill":"aspire"}}""");
            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetError(error);

            Assert.Equal(0, await AgentTelemetryHook.RunAsync(input, output, _ => throw new InvalidOperationException()));
            Assert.Contains(AgentTelemetryHook.PayloadLimitEnvironmentVariable, error.ToString());
            Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        });
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
