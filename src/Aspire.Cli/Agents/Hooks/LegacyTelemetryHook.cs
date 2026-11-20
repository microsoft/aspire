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
    /// Reads a client's user-level telemetry hook configuration and reports whether it registers a legacy script
    /// in the location that client's hook writer replaces.
    /// </summary>
    /// <param name="configPath">The client's user-level hook configuration file.</param>
    /// <param name="eventName">The client's post-tool-use event name, such as <c>postToolUse</c> or <c>PostToolUse</c>.</param>
    /// <param name="nestedHooks">
    /// <see langword="true"/> when hooks are grouped under each entry's <c>hooks</c> array, as in Claude Code;
    /// <see langword="false"/> when each entry is a hook, as in GitHub Copilot.
    /// </param>
    /// <param name="allowDuplicateProperties">
    /// <see langword="true"/> to resolve a repeated property name to its last value, as JavaScript's <c>JSON.parse</c>
    /// does; <see langword="false"/> to treat a repeated property name as malformed JSON.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <exception cref="JsonException">
    /// The file is not valid JSON, or repeats a property name when <paramref name="allowDuplicateProperties"/> is <see langword="false"/>.
    /// </exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be read.</exception>
    internal static async Task<bool> IsRegisteredAsync(
        string configPath, string eventName, bool nestedHooks, bool allowDuplicateProperties, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(configPath, cancellationToken);

        // JsonDocument rather than JsonNode: JsonObject throws on first lookup of a repeated property name,
        // while JsonDocument keeps every occurrence so the last one can win.
        using var document = JsonDocument.Parse(
            content,
            allowDuplicateProperties ? new JsonDocumentOptions { AllowDuplicateProperties = true } : TelemetryHookJson.ParseOptions);
        if (GetProperty(document.RootElement, "hooks") is not { } events ||
            GetProperty(events, eventName) is not { ValueKind: JsonValueKind.Array } entries)
        {
            return false;
        }

        // Copilot: {"hooks":{"postToolUse":[{"bash":"bash '/home/me/.aspire/hooks/track-telemetry.sh'"}]}}
        // Claude: {"hooks":{"PostToolUse":[{"matcher":"*","hooks":[{"command":"bash","args":["/home/me/.aspire/hooks/track-telemetry.sh"]}]}]}}
        foreach (var entry in entries.EnumerateArray())
        {
            if (!nestedHooks)
            {
                if (IsMatch(entry))
                {
                    return true;
                }
            }
            else if (GetProperty(entry, "hooks") is { ValueKind: JsonValueKind.Array } group &&
                group.EnumerateArray().Any(IsMatch))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsMatch(JsonObject hook)
        => ReferencesScript(hook["command"])
            || ReferencesScript(hook["bash"])
            || ReferencesScript(hook["powershell"])
            || (hook["args"] is JsonArray args && args.Any(ReferencesScript));

    private static bool IsMatch(JsonElement hook)
        => ReferencesScript(GetProperty(hook, "command"))
            || ReferencesScript(GetProperty(hook, "bash"))
            || ReferencesScript(GetProperty(hook, "powershell"))
            || (GetProperty(hook, "args") is { ValueKind: JsonValueKind.Array } args && args.EnumerateArray().Any(arg => ReferencesScript(arg)));

    // Returns the last value for a repeated name, matching JSON.parse. A strictly parsed document has at most one.
    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        JsonElement? value = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                value = property.Value;
            }
        }

        return value;
    }

    private static bool ReferencesScript(JsonElement? element)
        => element is { ValueKind: JsonValueKind.String } value && ReferencesScript(value.GetString()!);

    private static bool ReferencesScript(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && ReferencesScript(text);

    private static bool ReferencesScript(string text)
    {
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
