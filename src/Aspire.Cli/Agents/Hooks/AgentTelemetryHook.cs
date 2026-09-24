// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Classifies hook input before telemetry providers and enrichment are initialized.
/// </summary>
internal sealed class AgentTelemetryHook(IEnvironment environment)
{
    internal const string PayloadLimitEnvironmentVariable = "ASPIRE_AGENT_TELEMETRY_MAX_PAYLOAD_CHARACTERS";
    // Bound memory before JSON parsing; TextReader counts UTF-16 characters, not bytes.
    internal const int DefaultMaxPayloadCharacters = 64 * 1024;
    internal const int MaximumPayloadCharacters = 1024 * 1024;
    private const string ContinueResponse = """{"continue":true}""";
    private static readonly string[] s_mcpPrefixes = ["aspire-", "mcp__aspire__", "mcp_aspire_"];

    internal static (string Command, string[] Args) GetCommand(string mode)
    {
        var command = Environment.ProcessPath ?? throw new InvalidOperationException("Could not resolve the CLI executable.");
        string[] args = ["agent", "telemetry", mode];
        if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            args = [Path.Combine(AppContext.BaseDirectory, "aspire.dll"), .. args];
        }
        return (command, args);
    }

    internal async Task<int> RunAsync(TextReader input, TextWriter output, TextWriter error, Func<string[], Task<int>> execute)
    {
        try
        {
            var optOut = environment.GetEnvironmentVariable(AspireCliTelemetry.TelemetryOptOutConfigKey);
            if (optOut is "1" || string.Equals(optOut, "true", StringComparison.OrdinalIgnoreCase))
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

            var args = Classify(new string(buffer, 0, length), environment.GetEnvironmentVariable("COPILOT_CLI"), maxPayloadCharacters);
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

    internal static string[]? Classify(string payload, string? copilotCli, int maxPayloadCharacters)
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
                if (AgentTelemetryCatalog.Bundled.Skills.Contains(skill))
                {
                    trackedEvent = ("skill_invocation", "--skill-name", skill);
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
                    if (!segments[i].Equals("skills", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var skill = segments[i + 1];
                    var relativePath = string.Join('/', segments[(i + 1)..]);
                    if (AgentTelemetryCatalog.Bundled.Skills.Contains(skill))
                    {
                        if (segments[^1].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                        {
                            trackedEvent = ("skill_invocation", "--skill-name", skill);
                        }
                        else if (AgentTelemetryCatalog.Bundled.References.Contains(relativePath))
                        {
                            trackedEvent = ("reference_file_read", "--file-reference", relativePath);
                        }
                    }
                    break;
                }
            }
            else
            {
                foreach (var prefix in s_mcpPrefixes)
                {
                    if (tool.StartsWith(prefix, StringComparison.Ordinal) && AgentTelemetryCatalog.Bundled.Tools.Contains(tool[prefix.Length..]))
                    {
                        trackedEvent = ("tool_invocation", "--tool-name", tool);
                        break;
                    }
                }
            }

            if (trackedEvent is not { } telemetryEvent)
            {
                return null;
            }

            var client = copilotCli == "1" ? "copilot-cli"
                : Property(data, "hook_event_name") is not null
                    ? IsVsCode(data) ? "vscode" : "claude-code"
                    : Property(data, "toolArgs") is not null ? "copilot-cli" : "unknown";
            var args = new List<string>
            {
                "agent", "telemetry", "--event-type", telemetryEvent.EventType, "--client-name", client,
                "--timestamp", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                telemetryEvent.Dimension, telemetryEvent.Value
            };
            var session = Text(data, "sessionId") ?? Text(data, "session_id");
            if (Guid.TryParseExact(session, "D", out _))
            {
                args.AddRange(["--session-id", session]);
            }
            return [.. args];
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
