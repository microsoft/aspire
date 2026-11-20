// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Recognizes telemetry hook registrations written by Aspire 13.5 and 13.6, which ran
/// <c>track-telemetry.sh</c> or <c>track-telemetry.ps1</c> from the Aspire home <c>hooks</c> directory
/// instead of invoking the CLI directly.
/// </summary>
internal static class LegacyTelemetryHook
{
    private static readonly string[] s_scriptPathSuffixes = ["/hooks/track-telemetry.sh", "/hooks/track-telemetry.ps1"];

    /// <summary>
    /// Reads a client's user-level telemetry hook configuration and reports whether it registers a legacy script.
    /// </summary>
    /// <exception cref="JsonException">The file is not valid JSON or repeats a property name.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be read.</exception>
    internal static async Task<bool> IsRegisteredAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(configPath, cancellationToken);

        // Parse the same way the Claude hook writer does, so `aspire doctor` only reports a registration
        // that `aspire agent init` can replace.
        return IsRegistered(JsonNode.Parse(content, documentOptions: ParseOptions));
    }

    /// <summary>
    /// Strict JSON options for hook configuration files. JsonObject materializes properties lazily, so
    /// a repeated key would otherwise throw <see cref="ArgumentException"/> on first lookup instead of
    /// failing at parse time as malformed JSON.
    /// </summary>
    internal static JsonDocumentOptions ParseOptions { get; } = new() { AllowDuplicateProperties = false };

    internal static bool IsMatch(JsonObject hook)
        => ReferencesScript(hook["command"])
            || ReferencesScript(hook["bash"])
            || ReferencesScript(hook["powershell"])
            || (hook["args"] is JsonArray args && args.Any(ReferencesScript));

    private static bool IsRegistered(JsonNode? config)
    {
        if (config is not JsonObject root || root["hooks"] is not JsonObject events)
        {
            return false;
        }

        // Copilot: {"hooks":{"postToolUse":[{"bash":"bash '/home/me/.aspire/hooks/track-telemetry.sh'"}]}}
        // Claude: {"hooks":{"PostToolUse":[{"matcher":"*","hooks":[{"command":"bash","args":["/home/me/.aspire/hooks/track-telemetry.sh"]}]}]}}
        foreach (var eventName in new[] { "postToolUse", "PostToolUse" })
        {
            if (events[eventName] is not JsonArray entries)
            {
                continue;
            }

            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (IsMatch(entry) || (entry["hooks"] is JsonArray hooks && hooks.OfType<JsonObject>().Any(IsMatch)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ReferencesScript(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return false;
        }

        // Aspire 13.5 and 13.6 wrote the script path as the final token of a shell command, single-quoted, or as a
        // discrete exec-form argument:
        //   bash '/home/me/.aspire/hooks/track-telemetry.sh'
        //   pwsh -NoProfile -ExecutionPolicy Bypass -File 'C:\Users\me\.aspire\hooks\track-telemetry.ps1'
        //   ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\Users\\me\\.aspire\\hooks\\track-telemetry.ps1"]
        // The Aspire home directory depends on the install route, so only the hooks directory and exact
        // file name are required. Matching the end of the value rejects user scripts such as
        // /company/hooks/track-telemetry.sh.backup.
        var normalized = text.Trim().TrimEnd('\'', '"').Replace('\\', '/');
        return s_scriptPathSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
