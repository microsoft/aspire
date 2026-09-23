// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Handles hook input before CLI startup, avoiding a shell and host initialization for unrelated tools.
/// </summary>
internal static class AgentTelemetryHook
{
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

    // Keep these privacy allowlists equivalent to the canonical bundled hooks. Script parity tests
    // cover classification, and the allowlist test catches additions made by bundle synchronization.
    internal static readonly string[] s_skills = ["aspire", "aspire-init", "aspireify", "aspire-orchestration", "aspire-deployment", "aspire-monitoring"];
    internal static readonly string[] s_tools =
    [
        "doctor", "execute_resource_command", "get_doc", "list_apphosts", "list_console_logs",
        "list_docs", "list_integrations", "list_resources", "list_structured_logs",
        "list_trace_structured_logs", "list_traces", "refresh_tools", "search_docs", "select_apphost"
    ];
    internal static readonly string[] s_references =
    [
        "aspire-deployment/references/aws.md",
        "aspire-deployment/references/azure.md",
        "aspire-deployment/references/cicd.md",
        "aspire-deployment/references/docker-compose.md",
        "aspire-deployment/references/github-actions-azure-csharp.yml",
        "aspire-deployment/references/github-actions-azure-typescript.yml",
        "aspire-deployment/references/javascript.md",
        "aspire-deployment/references/kubernetes.md",
        "aspire-deployment/references/preflight.md",
        "aspire-init/references/init-workflow.md",
        "aspire-init/references/templates.md",
        "aspire-monitoring/references/diagnostics-bridge.md",
        "aspire-monitoring/references/monitoring.md",
        "aspire-monitoring/references/playwright-handoff.md",
        "aspire-orchestration/references/agent-workflows.md",
        "aspire-orchestration/references/app-commands.md",
        "aspire-orchestration/references/detection.md",
        "aspire-orchestration/references/resource-management.md",
        "aspire-orchestration/references/safety-guardrails.md",
        "aspire/references/aspire-13-3-breaking-changes.md",
        "aspire/references/aspire-13-5-breaking-changes.md",
        "aspireify/references/apphost-wiring.md",
        "aspireify/references/csharp-authoring.md",
        "aspireify/references/docker-compose.md",
        "aspireify/references/full-solution-apphosts.md",
        "aspireify/references/javascript-apps.md",
        "aspireify/references/opentelemetry.md",
        "aspireify/references/scan-and-propose.md",
        "aspireify/references/service-defaults.md",
        "aspireify/references/typescript-authoring.md",
        "aspireify/references/validation.md"
    ];

    internal static async Task<int> RunAsync(TextReader input, TextWriter output, Func<string[], Task<int>> execute)
    {
        try
        {
            var optOut = Environment.GetEnvironmentVariable(AspireCliTelemetry.TelemetryOptOutConfigKey);
            if (optOut is "1" || string.Equals(optOut, "true", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var buffer = new char[65537];
            var length = await input.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (length == buffer.Length)
            {
                while (await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
                {
                }
                return 0;
            }

            var args = Classify(new string(buffer, 0, length), Environment.GetEnvironmentVariable("COPILOT_CLI"));
            if (args is not null)
            {
                await execute(args).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Hooks must not interrupt the tool loop, including on malformed input or CLI failure.
            // Do not include the payload in diagnostics.
            await Console.Error.WriteLineAsync($"Agent telemetry hook failed ({ex.GetType().Name}).").ConfigureAwait(false);
        }
        finally
        {
            await output.WriteLineAsync("{\"continue\":true}").ConfigureAwait(false);
        }

        return 0;
    }

    internal static string[]? Classify(string payload, string? copilotCli)
    {
        if (payload.Length is 0 or > 65536)
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
            string? eventType = null;
            string? dimension = null;
            string? value = null;
            if (tool.Equals("skill", StringComparison.OrdinalIgnoreCase))
            {
                var skill = Text(input, "skill") ?? "";
                if (skill.StartsWith("aspire:", StringComparison.Ordinal))
                {
                    skill = skill[7..];
                }
                if (s_skills.Contains(skill, StringComparer.OrdinalIgnoreCase))
                {
                    (eventType, dimension, value) = ("skill_invocation", "--skill-name", skill);
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
                    if (s_skills.Contains(skill, StringComparer.OrdinalIgnoreCase))
                    {
                        if (segments[^1].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                        {
                            (eventType, dimension, value) = ("skill_invocation", "--skill-name", skill);
                        }
                        else if (s_references.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                        {
                            (eventType, dimension, value) = ("reference_file_read", "--file-reference", relativePath);
                        }
                    }
                    break;
                }
            }
            else
            {
                foreach (var prefix in new[] { "aspire-", "mcp__aspire__", "mcp_aspire_" })
                {
                    if (tool.StartsWith(prefix, StringComparison.Ordinal) && s_tools.Contains(tool[prefix.Length..], StringComparer.OrdinalIgnoreCase))
                    {
                        (eventType, dimension, value) = ("tool_invocation", "--tool-name", tool);
                        break;
                    }
                }
            }

            if (eventType is null)
            {
                return null;
            }

            var client = copilotCli == "1" ? "copilot-cli"
                : Property(data, "hook_event_name") is not null
                    ? IsVsCode(data) ? "vscode" : "claude-code"
                    : Property(data, "toolArgs") is not null ? "copilot-cli" : "unknown";
            var args = new List<string>
            {
                "agent", "telemetry", "--event-type", eventType, "--client-name", client,
                "--timestamp", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                dimension!, value!
            };
            var session = Text(data, "sessionId") ?? Text(data, "session_id");
            if (session?.Length == 36 && Guid.TryParseExact(session, "D", out _))
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
