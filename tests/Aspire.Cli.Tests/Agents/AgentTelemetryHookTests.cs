// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Mcp;
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
        Assert.Null(AgentTelemetryHook.Classify(payload, new TestEnvironment(), AgentTelemetryHook.DefaultMaxPayloadCharacters));
    }

    [Theory]
    [InlineData("toolName", "toolArgs", "skill", """{"skill":"aspire"}""", "aspire")]
    [InlineData("tool_name", "tool_input", "Skill", """{"skill":"aspire:aspire"}""", "aspire")]
    [InlineData("toolName", "toolArgs", "skill", """{"skill":"aspire-terminals"}""", "aspire-terminals")]
    [InlineData("tool_name", "tool_input", "Skill", """{"skill":"aspire:aspire-terminals"}""", "aspire-terminals")]
    public void SkillEventsPreserveArguments(string nameProperty, string inputProperty, string tool, string input, string expectedSkill)
    {
        var args = AgentTelemetryHook.Classify($$"""{"{{nameProperty}}":"{{tool}}","{{inputProperty}}":{{input}}}""", new TestEnvironment(), AgentTelemetryHook.DefaultMaxPayloadCharacters);
        Assert.NotNull(args);
        Assert.Equal(expectedSkill, args[Array.IndexOf(args, "--skill-name") + 1]);
        Assert.Equal("skill_invocation", args[Array.IndexOf(args, "--event-type") + 1]);
    }

    [Theory]
    [InlineData("aspire-terminals/references/apphost-terminals.md")]
    [InlineData("aspire-terminals/references/automation-and-cli.md")]
    [InlineData("aspire-terminals/references/resource-terminals.md")]
    [InlineData("aspire-terminals/references/tapes.md")]
    public void ReferenceFileEventsForwardRelativePath(string referencePath)
    {
        var args = AgentTelemetryHook.Classify(
            $$$"""{"tool_name":"Read","tool_input":{"file_path":"workspace/.agents/skills/{{{referencePath}}}"}}""",
            new TestEnvironment(),
            AgentTelemetryHook.DefaultMaxPayloadCharacters);

        Assert.NotNull(args);
        Assert.Equal("reference_file_read", args[Array.IndexOf(args, "--event-type") + 1]);
        Assert.Equal(referencePath, args[Array.IndexOf(args, "--file-reference") + 1]);
    }

    [Theory]
    [InlineData("github_copilot_app_agent", "copilot-app")]
    [InlineData("another_agent", "copilot-cli")]
    [InlineData(null, "copilot-cli")]
    public void SkillEventsUseAppMarkerBeforeCopilotCliFallback(string? aiAgent, string expectedClient)
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["AI_AGENT"] = aiAgent
        });
        var args = AgentTelemetryHook.Classify(
            """{"toolName":"skill","toolArgs":"{\"skill\":\"aspire\"}"}""",
            environment,
            AgentTelemetryHook.DefaultMaxPayloadCharacters);

        Assert.NotNull(args);
        Assert.Equal(expectedClient, args[Array.IndexOf(args, "--client-name") + 1]);
    }

    [Fact]
    public void OversizedPayloadIsIgnored()
    {
        const string payload = """{"toolName":"skill","toolArgs":{"skill":"aspire"}}""";
        Assert.NotNull(AgentTelemetryHook.Classify(payload, new TestEnvironment(), payload.Length));
        Assert.Null(AgentTelemetryHook.Classify(payload, new TestEnvironment(), payload.Length - 1));
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
        var args = AgentTelemetryHook.Classify("""{"toolName":"aspire-list_resources","toolArgs":"not json"}""", new TestEnvironment(), AgentTelemetryHook.DefaultMaxPayloadCharacters);
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
            new TestEnvironment(), AgentTelemetryHook.DefaultMaxPayloadCharacters);
        Assert.NotNull(args);
        var index = Array.IndexOf(args, "--session-id");
        Assert.Equal(expected, index >= 0);
        if (expected)
        {
            Assert.Equal(sessionId, args[index + 1]);
        }
    }

    [Theory]
    [InlineData("1", null, "copilot-cli")]
    [InlineData("TrUe", null, "copilot-cli")]
    [InlineData(null, "github_copilot_app_agent", "copilot-app")]
    [InlineData("1", "github_copilot_app_agent", "copilot-app")]
    [InlineData("1", "another_agent", "copilot-cli")]
    [InlineData(null, "another_agent", "claude-code")]
    public async Task HookUsesInjectedClientEnvironmentAndReturnsBenignResponseOnFailure(string? copilotCli, string? aiAgent, string expectedClient)
    {
        var hook = new AgentTelemetryHook(new TestEnvironment(new Dictionary<string, string?>
        {
            ["COPILOT_CLI"] = copilotCli,
            ["AI_AGENT"] = aiAgent
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
        Assert.Equal(expectedClient, recordedArgs[Array.IndexOf(recordedArgs, "--client-name") + 1]);
        Assert.Equal("""{"continue":true}""" + Environment.NewLine, output.ToString());
        Assert.Equal("Agent telemetry hook failed (IOException)." + Environment.NewLine, error.ToString());
    }

    [Theory]
    [InlineData("skill", true)]
    [InlineData("Skill", true)]
    [InlineData("view", false)]
    [InlineData("Read", false)]
    [InlineData("read_file", false)]
    [InlineData("bash", false)]
    [InlineData("Bash", false)]
    [InlineData("edit", false)]
    [InlineData("Edit", false)]
    [InlineData("grep", false)]
    [InlineData("powershell", false)]
    [InlineData("NotebookRead", false)]
    [InlineData("skills", false)]
    [InlineData("aspire-", false)]
    [InlineData("mcp__other__list_resources", false)]
    public void CopilotToolMatcherSelectsOnlyTrackedTools(string toolName, bool expected)
    {
        Assert.Equal(expected, MatchesAsCopilot(toolName));
    }

    [Theory]
    [InlineData("Skill", true, false)]
    [InlineData("Read", false, true)]
    [InlineData("mcp__aspire__list_resources", true, false)]
    [InlineData("skill", false, false)]
    [InlineData("view", false, false)]
    [InlineData("Bash", false, false)]
    [InlineData("NotebookRead", false, false)]
    [InlineData("ReadMe", false, false)]
    [InlineData("mcp__other__list_resources", false, false)]
    public void ClaudeMatchersSelectOnlyTrackedTools(string toolName, bool expectedTool, bool expectedRead)
    {
        Assert.Equal(expectedTool, MatchesAsClaude(toolName, ClaudeCodeAgentClient.TelemetryHookToolMatcher));
        Assert.Equal(expectedRead, MatchesAsClaude(toolName, ClaudeCodeAgentClient.TelemetryHookReadMatcher));
    }

    [Fact]
    public void ToolMatchersSelectEveryCanonicalMcpToolForEveryClientPrefix()
    {
        foreach (var prefix in new[] { "aspire-", "mcp__aspire__", "mcp_aspire_" })
        {
            foreach (var tool in AgentTelemetryCatalog.Canonical.Tools)
            {
                var toolName = prefix + tool;
                Assert.True(MatchesAsCopilot(toolName), toolName);
                Assert.True(MatchesAsClaude(toolName, ClaudeCodeAgentClient.TelemetryHookToolMatcher), toolName);
                Assert.NotNull(AgentTelemetryHook.Classify(
                    $$$"""{"toolName":"{{{toolName}}}","toolArgs":{}}""",
                    new TestEnvironment(),
                    AgentTelemetryHook.DefaultMaxPayloadCharacters));
            }
        }
    }

    [Fact]
    public void ClaudeReadIfSelectsEveryCanonicalSkillFile()
    {
        // Claude normalizes Windows paths to POSIX form before matching, so C:\Users\user becomes /c/Users/user.
        var paths = AgentTelemetryCatalog.Canonical.Skills.Select(skill => $"/c/Users/user/.claude/skills/{skill}/SKILL.md")
            .Concat(AgentTelemetryCatalog.Canonical.References.Select(reference => $"/home/user/.claude/plugins/cache/marketplace/aspire/1.0.0/skills/{reference}"));
        foreach (var path in paths)
        {
            Assert.True(MatchesClaudeReadIf(path), path);
            Assert.NotNull(AgentTelemetryHook.Classify(
                $$$"""{"tool_name":"Read","tool_input":{"file_path":"{{{path}}}"}}""",
                new TestEnvironment(),
                AgentTelemetryHook.DefaultMaxPayloadCharacters));
        }

        Assert.False(MatchesClaudeReadIf("/home/user/repo/skills/third-party/SKILL.md"));
    }

    [Fact]
    public void CanonicalCatalogCoversKnownMcpTools()
    {
        Assert.Equal(KnownMcpTools.All.Order(), AgentTelemetryCatalog.Canonical.Tools.Order());
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
        Assert.Equal(Values(script, "AspireSkills"), AgentTelemetryCatalog.Canonical.Skills.Order());
        Assert.Equal(Values(script, "AspireMcpTools"), AgentTelemetryCatalog.Canonical.Tools.Order());
        Assert.Equal(Values(script, "AspireReferenceFiles"), AgentTelemetryCatalog.Canonical.References.Order());
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
        Assert.Null(AgentTelemetryHook.Classify(padded, new TestEnvironment(), AgentTelemetryHook.DefaultMaxPayloadCharacters));
        Assert.NotNull(AgentTelemetryHook.Classify(padded, new TestEnvironment(), padded.Length));
    }

    // Both clients evaluate matchers as JavaScript regular expressions. Copilot wraps the pattern as
    // ^(?:PATTERN)$ and Claude searches unanchored, so emulate both with ECMAScript semantics.
    // https://docs.github.com/en/copilot/reference/hooks-reference
    // https://code.claude.com/docs/en/hooks#matcher-patterns
    private static bool MatchesAsCopilot(string toolName) =>
        Regex.IsMatch(toolName, $"^(?:{CopilotAgentClient.TelemetryHookMatcher})$", RegexOptions.ECMAScript);

    private static bool MatchesAsClaude(string toolName, string matcher) =>
        Regex.IsMatch(toolName, matcher, RegexOptions.ECMAScript);

    // Emulates the subset of Claude permission-rule globs that TelemetryHookReadIf uses. "//" anchors the pattern at
    // the filesystem root, "**/" spans any number of directories, and "*" stays within one path segment:
    //   Read(//**/skills/aspire*/**) matches /c/Users/user/.claude/skills/aspire-init/SKILL.md
    // https://code.claude.com/docs/en/permissions
    private static bool MatchesClaudeReadIf(string path)
    {
        const string RulePrefix = "Read(/";
        Assert.StartsWith(RulePrefix + "/", ClaudeCodeAgentClient.TelemetryHookReadIf);
        Assert.EndsWith(")", ClaudeCodeAgentClient.TelemetryHookReadIf);
        var glob = ClaudeCodeAgentClient.TelemetryHookReadIf[RulePrefix.Length..^1];
        var pattern = Regex.Escape(glob)
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"/\*\*", "/.*")
            .Replace(@"\*", "[^/]*");
        return Regex.IsMatch(path, $"^{pattern}$");
    }
}
