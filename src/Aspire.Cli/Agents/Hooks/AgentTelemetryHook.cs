// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Classifies hook input before telemetry providers and enrichment are initialized.
/// </summary>
internal sealed class AgentTelemetryHook(IEnvironment environment, CliExecutionContext executionContext)
{
    private const string SkillsDirectoryName = "skills";
    private const string SkillFileName = "SKILL.md";
    internal const string PayloadLimitEnvironmentVariable = "ASPIRE_AGENT_TELEMETRY_MAX_PAYLOAD_CHARACTERS";
    // Bound memory before JSON parsing; TextReader counts UTF-16 characters, not bytes.
    internal const int DefaultMaxPayloadCharacters = 64 * 1024;
    internal const int MaximumPayloadCharacters = 1024 * 1024;
    private const string ContinueResponse = """{"continue":true}""";
    private static readonly string[] s_mcpPrefixes = ["aspire-", "mcp__aspire__", "mcp_aspire_"];

    // Matches the tool names Classify recognizes through s_mcpPrefixes, for example aspire-list_resources
    // (Copilot), mcp__aspire__list_resources (Claude Code), and mcp_aspire_list_resources (VS Code).
    // Clients build their hook matchers from this so they cannot drift from the classifier.
    internal static string McpMatcher => string.Join('|', s_mcpPrefixes.Select(prefix => prefix + ".+"));

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
    /// <param name="clientId">The <see cref="IAgentClient.Id"/> whose hook configuration runs the command.</param>
    /// <param name="scope">Where the hook configuration is installed.</param>
    /// <remarks>
    /// User and project hooks run the same CLI invocation. <c>--source</c> and <c>--scope</c> let
    /// <see cref="ShouldRecord"/> detect when another installed hook records the same event.
    /// </remarks>
    internal static (string Command, string[] Args) GetHookCommand(string clientId, AgentConfigurationScope scope)
    {
        var (command, args) = GetCommand(AgentTelemetryProtocol.HookOptionName);
        return (command,
        [
            .. args,
            AgentTelemetryProtocol.SourceOptionName, clientId,
            AgentTelemetryProtocol.ScopeOptionName, scope == AgentConfigurationScope.Project ? AgentTelemetryProtocol.ProjectScope : AgentTelemetryProtocol.UserScope
        ]);
    }

    internal async Task<int> RunAsync(TextReader input, TextWriter output, TextWriter error, Func<string[], Task<int>> execute)
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
            if (args is not null)
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
    /// Determines whether a hook handler's executable and arguments run Aspire's telemetry hook.
    /// </summary>
    /// <remarks>
    /// Each agent client owns its hook file layout and passes the handler's executable and arguments here, so this
    /// matches only the CLI invocation that <see cref="GetHookCommand"/> writes, plus legacy hook scripts.
    /// </remarks>
    internal static bool IsHookInvocation(JsonNode? executableNode, JsonNode? argsNode)
    {
        // Match legacy scripts or our exact CLI invocation, not arbitrary Aspire commands. Trailing
        // arguments are allowed because current hooks append --source and --scope.
        var executable = GetString(executableNode);
        if (ReferencesTelemetryScript(executable))
        {
            return true;
        }
        if (argsNode is JsonArray args)
        {
            var values = args.Select(GetString).ToArray();
            var (currentCommand, _) = GetCommand(AgentTelemetryProtocol.HookOptionName);
            var executableName = Path.GetFileNameWithoutExtension(executable);
            var isDotnet = string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase);
            var isDirectAspireInvocation = !isDotnet
                && values is [AgentTelemetryProtocol.AgentCommandName, AgentTelemetryProtocol.TelemetryCommandName, AgentTelemetryProtocol.HookOptionName, ..]
                && (executable == currentCommand || string.Equals(executableName, "aspire", StringComparison.OrdinalIgnoreCase));
            var isManagedAspireInvocation = isDotnet
                && values is [var assemblyPath, AgentTelemetryProtocol.AgentCommandName, AgentTelemetryProtocol.TelemetryCommandName, AgentTelemetryProtocol.HookOptionName, ..]
                && string.Equals(Path.GetFileName(assemblyPath), "aspire.dll", StringComparison.OrdinalIgnoreCase);

            return isDirectAspireInvocation || isManagedAspireInvocation || values.Any(ReferencesTelemetryScript);
        }

        return false;
    }

    /// <summary>
    /// Determines whether the hook identified by <paramref name="source"/> and <paramref name="scope"/> records
    /// the tool call, or leaves it to another installed Aspire hook that runs for the same call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Agents run every configured hook for a tool call without deduplicating them. GitHub Copilot runs hooks from
    /// <c>~/.copilot/hooks</c> and, in a trusted repository, from <c>.github/hooks</c> and <c>.claude/settings.json</c>.
    /// Claude Code runs hooks from its user and project settings.
    /// https://docs.github.com/en/copilot/reference/hooks-reference#hooks-locations
    /// https://code.claude.com/docs/en/settings#settings-files
    /// </para>
    /// <para>
    /// A Claude Code hook skips inside Copilot, which never reads Claude user settings, so it can only be a
    /// repository hook running next to Copilot's own. A project hook skips when its client also has the user hook,
    /// because user hooks run in every folder while project hooks need a trusted repository. Hooks installed
    /// before <c>--source</c> and <c>--scope</c> existed pass neither, and unreadable user settings never cause a
    /// skip, so a hook only skips when another hook is known to record the call.
    /// </para>
    /// </remarks>
    internal bool ShouldRecord(string? source, string? scope)
    {
        if (source is null || scope is null)
        {
            return true;
        }

        if (source == ClaudeCodeAgentClient.ClientId && GetCopilotRuntimeName(environment) is not null)
        {
            return false;
        }

        if (scope != AgentTelemetryProtocol.ProjectScope)
        {
            return true;
        }

        try
        {
            return !HasUserHook(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    // Reads only the client's user hook location, never the repository, because this runs on every recorded tool call.
    private bool HasUserHook(string source)
    {
        switch (source)
        {
            case CopilotAgentClient.ClientId:
                // Aspire owns this file, so its presence means the user hook is installed.
                var copilotDirectory = AgentPath.Expand(CopilotPaths.GetConfigDirectory(executionContext.HomeDirectory, environment), executionContext);
                return File.Exists(Path.Combine(copilotDirectory, "hooks", CopilotAgentClient.TelemetryHookFileName));

            case ClaudeCodeAgentClient.ClientId:
                // Claude nests handlers in matcher groups:
                // {"hooks":{"PostToolUse":[{"matcher":"...","hooks":[{"command":"/path/to/aspire","args":[...]}]}]}}
                // A project hook installed in the home directory shares this file, so it does not count as the user hook.
                var settingsPath = Path.Combine(ClaudeCodePaths.GetConfigDirectory(executionContext, environment), "settings.json");
                return File.Exists(settingsPath)
                    && JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings
                    && settings["hooks"] is JsonObject hooks
                    && hooks["PostToolUse"] is JsonArray postToolUse
                    && postToolUse.OfType<JsonObject>()
                        .SelectMany(group => group["hooks"] as JsonArray ?? [])
                        .OfType<JsonObject>()
                        .Any(hook => IsHookInvocation(hook["command"], hook["args"]) && !IsProjectScoped(hook["args"]));

            default:
                return false;
        }
    }

    // Hook args end with: ... --source claude --scope project
    private static bool IsProjectScoped(JsonNode? argsNode)
        => argsNode is JsonArray args
            && args.Select(GetString).SkipWhile(arg => arg != AgentTelemetryProtocol.ScopeOptionName).Skip(1).FirstOrDefault() == AgentTelemetryProtocol.ProjectScope;

    private static string? GetString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool ReferencesTelemetryScript(string? value)
        => value is not null
            && (value.Contains(TelemetryHookInstaller.ShellResourceName, StringComparison.OrdinalIgnoreCase)
                || value.Contains(TelemetryHookInstaller.PowerShellResourceName, StringComparison.OrdinalIgnoreCase));

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
            }
            return [.. args];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the client name when the hook runs inside a GitHub Copilot runtime, or <see langword="null"/> otherwise.
    /// </summary>
    // The app also sets COPILOT_CLI, so its specific runtime marker must take precedence.
    internal static string? GetCopilotRuntimeName(IEnvironment environment) =>
        environment.GetEnvironmentVariable(CopilotAppInstallationDetector.AgentEnvironmentVariable) switch
        {
            CopilotAppInstallationDetector.AgentEnvironmentValue => "copilot-app",
            _ when environment.IsFlagEnabled("COPILOT_CLI") => "copilot-cli",
            _ => null
        };

    private static string GetClientName(JsonElement data, IEnvironment environment) =>
        GetCopilotRuntimeName(environment) switch
        {
            { } copilotRuntimeName => copilotRuntimeName,
            _ when Property(data, "hook_event_name") is not null => IsVsCode(data) ? "vscode" : "claude-code",
            _ when Property(data, "toolArgs") is not null => "copilot-cli",
            _ => "unknown"
        };

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
