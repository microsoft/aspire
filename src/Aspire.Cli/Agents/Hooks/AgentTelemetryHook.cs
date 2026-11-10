// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Classifies hook input before telemetry providers and enrichment are initialized.
/// </summary>
internal sealed class AgentTelemetryHook(IEnvironment environment)
{
    private const string SkillsDirectoryName = "skills";
    private const string SkillFileName = "SKILL.md";
    internal const string PayloadLimitEnvironmentVariable = "ASPIRE_AGENT_TELEMETRY_MAX_PAYLOAD_CHARACTERS";
    // Bound memory before JSON parsing; TextReader counts UTF-16 characters, not bytes.
    internal const int DefaultMaxPayloadCharacters = 64 * 1024;
    internal const int MaximumPayloadCharacters = 1024 * 1024;
    private const string ContinueResponse = """{"continue":true}""";
    private static readonly string[] s_mcpPrefixes = ["aspire-", "mcp__aspire__", "mcp_aspire_"];

    /// <summary>
    /// Copilot user-level <c>postToolUse</c> <c>matcher</c> for the skill and Aspire MCP tools.
    /// </summary>
    /// <remarks>
    /// Copilot matchers see only the tool name, so matching file-read tools would launch the CLI for every
    /// read in every repository. Copilot anchors the pattern itself, and the explicit anchors keep it identical
    /// in shape to the Claude patterns (https://docs.github.com/en/copilot/reference/hooks-reference).
    /// </remarks>
    internal static string CopilotToolMatcher { get; } = $"^(?:[Ss]kill|{McpMatcher})$";

    /// <summary>
    /// Claude Code <c>PostToolUse</c> <c>matcher</c> for the Skill and Aspire MCP tools. Reads use <see cref="ClaudeReadIf"/>.
    /// </summary>
    /// <remarks>
    /// Claude Code matches unanchored, so the explicit anchors keep <c>Skill</c> from matching other tool names
    /// (https://code.claude.com/docs/en/hooks#matcher-patterns).
    /// </remarks>
    internal static string ClaudeToolMatcher { get; } = $"^(?:Skill|{McpMatcher})$";

    internal const string ClaudeReadMatcher = "^Read$";

    /// <summary>
    /// Claude Code handler <c>if</c> rule that limits Read hooks to files under an Aspire skill directory.
    /// </summary>
    /// <remarks>
    /// Every canonical skill name starts with <c>aspire</c>. <c>//**/</c> matches at any absolute depth, so this covers
    /// plugin, user, and project skill installs (https://code.claude.com/docs/en/hooks#filter-by-tool-name-and-arguments-with-the-if-field).
    /// </remarks>
    internal const string ClaudeReadIf = "Read(//**/skills/aspire*/**)";

    // Matches the tool names Classify recognizes through s_mcpPrefixes, for example aspire-list_resources
    // (Copilot), mcp__aspire__list_resources (Claude Code), and mcp_aspire_list_resources (VS Code).
    private static string McpMatcher => string.Join('|', s_mcpPrefixes.Select(prefix => prefix + ".+"));

    internal static (string Command, string[] Args) GetCommand(string mode)
    {
        var command = Environment.ProcessPath ?? throw new InvalidOperationException("Could not resolve the CLI executable.");
        string[] args = [AgentTelemetryProtocol.AgentCommandName, AgentTelemetryProtocol.TelemetryCommandName, mode];
        if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            args = [Path.Combine(AppContext.BaseDirectory, "aspire.dll"), .. args];
        }
        return (command, args);
    }

    /// <summary>
    /// Gets the hook command written into an agent's hook configuration.
    /// </summary>
    /// <param name="source">The hook configuration format, <see cref="AgentTelemetryProtocol.CopilotSource"/> or <see cref="AgentTelemetryProtocol.ClaudeSource"/>.</param>
    /// <param name="scope">Where the hook configuration is installed.</param>
    /// <remarks>
    /// User and project hooks run the same CLI invocation. <c>--source</c> and <c>--scope</c> let the hook detect
    /// when another installed hook owns the same event (see <see cref="ShouldSkip"/>).
    /// </remarks>
    internal static (string Command, string[] Args) GetHookCommand(string source, AgentConfigurationScope scope)
    {
        var (command, args) = GetCommand(AgentTelemetryProtocol.HookOptionName);
        return (command,
        [
            .. args,
            AgentTelemetryProtocol.SourceOptionName, source,
            AgentTelemetryProtocol.ScopeOptionName, scope == AgentConfigurationScope.Project ? AgentTelemetryProtocol.ProjectScope : AgentTelemetryProtocol.UserScope
        ]);
    }

    /// <param name="input">The hook payload.</param>
    /// <param name="output">Receives the hook response.</param>
    /// <param name="error">Receives diagnostics.</param>
    /// <param name="source">The <c>--source</c> value, or <see langword="null"/> for hooks installed without one.</param>
    /// <param name="scope">The <c>--scope</c> value, or <see langword="null"/> for hooks installed without one.</param>
    /// <param name="workingDirectory">The directory the agent ran the hook in, used to find project hooks.</param>
    /// <param name="execute">Records the classified event.</param>
    internal async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        string? source,
        string? scope,
        string workingDirectory,
        Func<string[], Task<int>> execute)
    {
        try
        {
            if (environment.IsFlagEnabled(AspireCliTelemetry.TelemetryOptOutConfigKey))
            {
                return 0;
            }

            int maxPayloadCharacters;
            try
            {
                maxPayloadCharacters = GetMaxPayloadCharacters(environment.GetEnvironmentVariable(PayloadLimitEnvironmentVariable));
            }
            catch (ArgumentException ex)
            {
                await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 0;
            }

            // One sentinel distinguishes a payload exactly at the limit from an oversized one.
            var buffer = new char[maxPayloadCharacters + 1];
            var length = await input.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (length == buffer.Length)
            {
                while (await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
                {
                }
                return 0;
            }

            var args = Classify(new string(buffer, 0, length), environment, maxPayloadCharacters);
            if (args is not null && !ShouldSkip(source, scope, workingDirectory))
            {
                await execute(args).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Hooks must not interrupt the tool loop, including on malformed input or CLI failure.
            // Do not include the payload in diagnostics.
            await error.WriteLineAsync($"Agent telemetry hook failed ({ex.GetType().Name}).").ConfigureAwait(false);
        }
        finally
        {
            await output.WriteLineAsync(ContinueResponse).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Determines whether another installed hook owns this event, so recording it here would double count.
    /// </summary>
    /// <remarks>
    /// Copilot CLI runs the repository's <c>.claude/settings.json</c> hooks alongside its own, so a Claude hook
    /// defers to the Copilot hook inside Copilot. A user hook defers to a project hook for the same source because
    /// agents run both without deduplication (https://docs.github.com/en/copilot/reference/hooks-reference#hooks-locations).
    /// Hooks installed without <c>--source</c> or <c>--scope</c> never skip. Failures fall back to recording.
    /// </remarks>
    private bool ShouldSkip(string? source, string? scope, string workingDirectory)
    {
        try
        {
            if (source == AgentTelemetryProtocol.ClaudeSource
                && (environment.IsFlagEnabled("COPILOT_CLI")
                    || string.Equals(
                        environment.GetEnvironmentVariable(CopilotAppInstallationDetector.AgentEnvironmentVariable),
                        CopilotAppInstallationDetector.AgentEnvironmentValue,
                        StringComparison.Ordinal)))
            {
                return true;
            }

            if (scope != AgentTelemetryProtocol.UserScope || FindRepositoryRoot(workingDirectory) is not { } root)
            {
                return false;
            }

            return source switch
            {
                AgentTelemetryProtocol.CopilotSource => HasProjectHook(
                    Path.Combine(root, ".github", "hooks", AgentTelemetryProtocol.CopilotHookFileName), "postToolUse", "exec", nested: false),
                AgentTelemetryProtocol.ClaudeSource => HasProjectHook(
                    Path.Combine(root, ".claude", "settings.json"), "PostToolUse", "command", nested: true),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    // A worktree or submodule has a .git file instead of a directory.
    private static string? FindRepositoryRoot(string workingDirectory)
    {
        for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    // Copilot hook files list handlers directly under the event:
    //   {"hooks":{"postToolUse":[{"exec":"/path/to/aspire","args":["agent","telemetry","--hook","--source","copilot","--scope","project"]}]}}
    // Claude settings nest handlers in matcher groups:
    //   {"hooks":{"PostToolUse":[{"matcher":"...","hooks":[{"command":"/path/to/aspire","args":[...,"--scope","project"]}]}]}}
    private static bool HasProjectHook(string path, string eventName, string executableProperty, bool nested)
    {
        if (!File.Exists(path)
            || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject settings
            || settings["hooks"] is not JsonObject hooks
            || hooks[eventName] is not JsonArray entries)
        {
            return false;
        }

        var handlers = nested
            ? entries.SelectMany(entry => entry is JsonObject { } group && group["hooks"] is JsonArray groupHooks ? groupHooks : [])
            : entries;

        return handlers.Any(handler => TelemetryHookSettings.IsAspireHook(handler, executableProperty)
            && handler!["args"] is JsonArray args
            && args.Select(arg => arg?.GetValueKind() == JsonValueKind.String ? arg.GetValue<string>() : null)
                .SkipWhile(arg => arg != AgentTelemetryProtocol.ScopeOptionName)
                .Skip(1)
                .FirstOrDefault() == AgentTelemetryProtocol.ProjectScope);
    }

    internal static int GetMaxPayloadCharacters(string? configuredValue)
    {
        if (configuredValue is null)
        {
            return DefaultMaxPayloadCharacters;
        }
        if (int.TryParse(configuredValue, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            && limit is > 0 and <= MaximumPayloadCharacters)
        {
            return limit;
        }

        throw new ArgumentException($"{PayloadLimitEnvironmentVariable} must be an integer between 1 and {MaximumPayloadCharacters} UTF-16 characters.");
    }

    internal static string[]? Classify(string payload, IEnvironment environment, int maxPayloadCharacters)
    {
        if (payload.Length == 0 || payload.Length > maxPayloadCharacters)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var data = document.RootElement;
            var tool = Text(data, "toolName") ?? Text(data, "tool_name");
            if (tool is null)
            {
                return null;
            }

            // Copilot: {"toolName":"skill","toolArgs":"{\"skill\":\"aspire\"}"};
            // Claude: {"tool_name":"Skill","tool_input":{"skill":"aspire:aspire"}}.
            var input = Property(data, "toolArgs") ?? Property(data, "tool_input") ?? default;
            using var nested = ParseInput(input);
            input = nested?.RootElement ?? input;
            (string EventType, string Dimension, string Value)? trackedEvent = null;
            if (tool.Equals("skill", StringComparison.OrdinalIgnoreCase))
            {
                var skill = Text(input, "skill") ?? "";
                if (skill.StartsWith("aspire:", StringComparison.Ordinal))
                {
                    skill = skill[7..];
                }
                if (AgentTelemetryCatalog.Canonical.Skills.Contains(skill))
                {
                    trackedEvent = (AgentTelemetryProtocol.SkillInvocationEventType, AgentTelemetryProtocol.SkillNameOptionName, skill);
                }
            }
            else if (tool.Equals("view", StringComparison.OrdinalIgnoreCase)
                || tool.Equals("Read", StringComparison.OrdinalIgnoreCase)
                || tool.Equals("read_file", StringComparison.OrdinalIgnoreCase))
            {
                var path = Text(input, "path") ?? Text(input, "filePath") ?? Text(input, "file_path") ?? "";
                var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var i = segments.Length - 3; i >= 0; i--)
                {
                    if (!segments[i].Equals(SkillsDirectoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var skill = segments[i + 1];
                    var relativePath = string.Join('/', segments[(i + 1)..]);
                    if (AgentTelemetryCatalog.Canonical.Skills.Contains(skill))
                    {
                        if (segments[^1].Equals(SkillFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            trackedEvent = (AgentTelemetryProtocol.SkillInvocationEventType, AgentTelemetryProtocol.SkillNameOptionName, skill);
                        }
                        else if (AgentTelemetryCatalog.Canonical.References.Contains(relativePath))
                        {
                            trackedEvent = (AgentTelemetryProtocol.ReferenceFileReadEventType, AgentTelemetryProtocol.FileReferenceOptionName, relativePath);
                        }
                    }
                    break;
                }
            }
            else
            {
                foreach (var prefix in s_mcpPrefixes)
                {
                    if (tool.StartsWith(prefix, StringComparison.Ordinal) && AgentTelemetryCatalog.Canonical.Tools.Contains(tool[prefix.Length..]))
                    {
                        trackedEvent = (AgentTelemetryProtocol.ToolInvocationEventType, AgentTelemetryProtocol.ToolNameOptionName, tool);
                        break;
                    }
                }
            }

            if (trackedEvent is not { } telemetryEvent)
            {
                return null;
            }

            var args = new List<string>
            {
                AgentTelemetryProtocol.AgentCommandName, AgentTelemetryProtocol.TelemetryCommandName,
                AgentTelemetryProtocol.EventTypeOptionName, telemetryEvent.EventType,
                AgentTelemetryProtocol.ClientNameOptionName, GetClientName(data, environment),
                AgentTelemetryProtocol.TimestampOptionName, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                telemetryEvent.Dimension, telemetryEvent.Value
            };
            var session = Text(data, "sessionId") ?? Text(data, "session_id");
            if (Guid.TryParseExact(session, "D", out _))
            {
                args.AddRange([AgentTelemetryProtocol.SessionIdOptionName, session]);

                // VS Code can deliver one tool call to both the Copilot and Claude hook files. Forward
                // its tool-use ID so the command can drop the second delivery. The ID is only a
                // deduplication key, is scoped to the session, and is never recorded.
                var toolUseId = Text(data, "tool_use_id");
                if (toolUseId is not null && IsToolUseId(toolUseId))
                {
                    args.AddRange([AgentTelemetryProtocol.ToolUseIdOptionName, toolUseId]);
                }
            }
            return [.. args];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The app also sets COPILOT_CLI, so its specific runtime marker must take precedence.
    private static string GetClientName(JsonElement data, IEnvironment environment) =>
        environment.GetEnvironmentVariable(CopilotAppInstallationDetector.AgentEnvironmentVariable) switch
        {
            CopilotAppInstallationDetector.AgentEnvironmentValue => "copilot-app",
            _ when environment.IsFlagEnabled("COPILOT_CLI") => "copilot-cli",
            _ when Property(data, "hook_event_name") is not null => IsVsCode(data) ? "vscode" : "claude-code",
            _ when Property(data, "toolArgs") is not null => "copilot-cli",
            _ => "unknown"
        };

    internal static bool IsToolUseId(string value)
        => value.Length is > 0 and <= 128 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsVsCode(JsonElement data)
    {
        var path = Text(data, "transcript_path")?.Replace('\\', '/') ?? "";
        return (Text(data, "tool_use_id") ?? "").Contains("__vscode", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Code/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Code - Insiders/", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument? ParseInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(input.GetString()!);
        }
        catch (JsonException)
        {
            // An MCP tool invocation is still classifiable when only its arguments are malformed.
            return null;
        }
    }

    private static JsonElement? Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrEmpty(value.GetString()) ? value.GetString() : null;
}
