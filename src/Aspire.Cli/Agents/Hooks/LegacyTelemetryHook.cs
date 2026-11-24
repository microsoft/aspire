// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Shared;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Recognizes telemetry hook registrations written by Aspire 13.5 and 13.6, which ran
/// <c>track-telemetry.sh</c> or <c>track-telemetry.ps1</c> from the Aspire home <c>hooks</c> directory
/// instead of invoking the CLI directly.
/// </summary>
internal sealed class LegacyTelemetryHook
{
    private static readonly string[] s_scriptNames = ["track-telemetry.sh", "track-telemetry.ps1"];

    // Shared by both IsMatch overloads so doctor detection and agent init removal inspect the same fields.
    private static readonly string[] s_commandPropertyNames = ["command", "bash", "powershell"];

    // Each script path, normalized to forward slashes.
    private readonly string[] _scriptPaths;

    // Every accepted shell-command spelling of each script path, normalized to forward slashes.
    private readonly string[] _scriptReferences;

    /// <summary>
    /// Creates a matcher for the scripts in every Aspire home directory that older CLIs could have used: the
    /// current CLI's Aspire home, which depends on the install route, the <c>ASPIRE_HOME</c> directory, and the
    /// default <c>~/.aspire</c> directory.
    /// </summary>
    internal LegacyTelemetryHook(CliExecutionContext context, IEnvironment environment)
    {
        var homes = new[]
        {
            context.AspireHomeDirectory.FullName,
            AspireHomeDirectory.GetDefault(
                environment.GetEnvironmentVariable(AspireHomeDirectory.EnvironmentVariable), context.HomeDirectory.FullName),
            Path.Combine(context.HomeDirectory.FullName, ".aspire"),
        };

        var scriptPaths = homes
            .SelectMany(home => s_scriptNames.Select(name => Path.Combine(home, "hooks", name)))
            .ToArray();
        _scriptPaths = scriptPaths
            .Select(Normalize)
            .Distinct(StringComparers.FileSystemPath)
            .ToArray();

        // Aspire 13.5 and 13.6 wrote shell-command hooks with the script path single-quoted as the final token,
        // escaping apostrophes for bash ('\'') or PowerShell (''):
        //   bash '/home/me/.aspire/hooks/track-telemetry.sh'
        //   pwsh -NoProfile -ExecutionPolicy Bypass -File 'C:\Users\me\.aspire\hooks\track-telemetry.ps1'
        _scriptReferences = scriptPaths
            .SelectMany(path => new[] { path, $"'{path.Replace("'", "'\\''")}'", $"'{path.Replace("'", "''")}'" })
            .Select(Normalize)
            .Distinct(StringComparers.FileSystemPath)
            .ToArray();
    }

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
    internal async Task<bool> IsRegisteredAsync(
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

    /// <summary>
    /// Reports whether a hook runs one of the legacy scripts this matcher owns.
    /// </summary>
    internal bool IsMatch(JsonObject hook)
        => s_commandPropertyNames.Any(name => ReferencesScript(GetString(hook[name])))
            || IsExecFormMatch(
                GetString(hook["command"]),
                hook["args"] is JsonArray args ? [.. args.Select(GetString)] : null);

    private bool IsMatch(JsonElement hook)
        => s_commandPropertyNames.Any(name => ReferencesScript(GetString(GetProperty(hook, name))))
            || IsExecFormMatch(
                GetString(GetProperty(hook, "command")),
                GetProperty(hook, "args") is { ValueKind: JsonValueKind.Array } args ? [.. args.EnumerateArray().Select(arg => GetString(arg))] : null);

    // Aspire 13.5 and 13.6 wrote Claude hooks in exec form, with the script as bash's only argument or as the
    // value of pwsh's final -File option:
    //   {"command":"bash","args":["/home/me/.aspire/hooks/track-telemetry.sh"]}
    //   {"command":"pwsh","args":["-NoProfile","-ExecutionPolicy","Bypass","-File","C:\\Users\\me\\.aspire\\hooks\\track-telemetry.ps1"]}
    // Any other shape, such as {"command":"cat","args":["/home/me/.aspire/hooks/track-telemetry.sh"]}, passes
    // the path as data rather than running the script, so it is left alone.
    private bool IsExecFormMatch(string? command, string?[]? args)
        => args is [.. var argsBeforeScript, var script] && RunsScript(command, argsBeforeScript) && IsScriptPath(script);

    // Shared by the exec-form and shell-form matchers so both accept the same invocations: the script as bash's
    // only argument, or as the value of pwsh's final -File option.
    private static bool RunsScript(string? command, string?[] argsBeforeScript)
        => argsBeforeScript is []
            ? string.Equals(command, "bash", StringComparison.OrdinalIgnoreCase)
            : string.Equals(command, "pwsh", StringComparison.OrdinalIgnoreCase)
                && string.Equals(argsBeforeScript[^1], "-File", StringComparison.OrdinalIgnoreCase);

    private bool IsScriptPath(string? value)
        => value is not null && _scriptPaths.Contains(Normalize(value), StringComparers.FileSystemPath);

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

    private static string? GetString(JsonElement? element)
        => element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string? GetString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private bool ReferencesScript(string? text)
    {
        if (text is null)
        {
            return false;
        }

        // Require an Aspire-owned script path as the whole value, or as the final whitespace-separated token of a
        // bash or pwsh invocation that RunsScript accepts. User hooks such as
        // `bash /work/project/hooks/track-telemetry.sh`, `bash '/home/me/.aspire/hooks/track-telemetry.sh.backup'`,
        // or `cat '/home/me/.aspire/hooks/track-telemetry.sh'` are left alone. Paths compare with the platform's
        // file-system rules, so a path that differs only by case is a different file on Linux.
        var value = Normalize(text.Trim());
        foreach (var reference in _scriptReferences)
        {
            if (!value.EndsWith(reference, StringComparisons.FileSystemPath))
            {
                continue;
            }

            var prefix = value[..^reference.Length];
            if (prefix.Length == 0 ||
                (char.IsWhiteSpace(prefix[^1]) &&
                    prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is [var command, .. var argsBeforeScript] &&
                    RunsScript(command, argsBeforeScript)))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value) => value.Replace('\\', '/');
}
