// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Merges Aspire telemetry hooks into the <c>hooks</c> object of a user-owned agent settings file.
/// </summary>
internal static class TelemetryHookSettings
{
    /// <summary>
    /// Applies <paramref name="update"/> to the settings file's <c>hooks</c> object and publishes the result.
    /// </summary>
    /// <param name="settingsPath">The settings file to merge into; created when absent.</param>
    /// <param name="logger">Receives diagnostics for skipped updates.</param>
    /// <param name="update">Mutates the hooks object, or returns <see langword="false"/> without mutating it when its shape is unexpected.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The reason the file was left unchanged, or <see langword="null"/> when the hooks were written.</returns>
    internal static async Task<TelemetryHookSkipReason?> UpdateAsync(
        string settingsPath, ILogger logger, Func<JsonObject, bool> update, CancellationToken cancellationToken)
    {
        var edit = new AgentConfiguration();
        byte[]? bytes;
        try
        {
            bytes = await edit.ReadBytesForEditAsync(settingsPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to read agent settings at {Path}.", settingsPath);
            return TelemetryHookSkipReason.WriteFailed;
        }

        JsonObject settings;
        if (bytes is not null)
        {
            using var reader = new StreamReader(new MemoryStream(bytes));
            var content = await reader.ReadToEndAsync(cancellationToken);
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(content);
            }
            catch (JsonException ex)
            {
                // Preserve malformed settings rather than replacing content we cannot merge.
                logger.LogDebug(ex, "Agent settings at {Path} contained malformed JSON; skipping hook registration.", settingsPath);
                return TelemetryHookSkipReason.MalformedConfig;
            }

            switch (parsed)
            {
                case null:
                    settings = new JsonObject();
                    break;
                case JsonObject existing:
                    settings = existing;
                    break;
                default:
                    return TelemetryHookSkipReason.UnexpectedConfigShape;
            }
        }
        else
        {
            settings = new JsonObject();
        }

        JsonObject hooks;
        if (settings.TryGetPropertyValue("hooks", out var hooksNode))
        {
            if (hooksNode is not JsonObject hooksObject)
            {
                return TelemetryHookSkipReason.UnexpectedConfigShape;
            }
            hooks = hooksObject;
        }
        else
        {
            hooks = new JsonObject();
            settings["hooks"] = hooks;
        }

        if (!update(hooks))
        {
            return TelemetryHookSkipReason.UnexpectedConfigShape;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await edit.PublishAsync(settingsPath, settings, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to write agent settings at {Path}.", settingsPath);
            return TelemetryHookSkipReason.WriteFailed;
        }
    }

    /// <summary>
    /// Gets the event's hook array, adding an empty one when the event is absent.
    /// </summary>
    /// <returns><see langword="false"/> when the event exists but is not an array.</returns>
    internal static bool TryGetEventArray(JsonObject hooks, string eventName, [NotNullWhen(true)] out JsonArray? entries)
    {
        if (!hooks.TryGetPropertyValue(eventName, out var node))
        {
            entries = new JsonArray();
            hooks[eventName] = entries;
            return true;
        }

        entries = node as JsonArray;
        return entries is not null;
    }

    /// <summary>
    /// Determines whether a hook handler runs Aspire's telemetry hook.
    /// </summary>
    /// <param name="node">The hook handler entry.</param>
    /// <param name="executableProperty">The handler property holding the executable: <c>command</c> for Claude, <c>exec</c> for Copilot.</param>
    internal static bool IsAspireHook(JsonNode? node, string executableProperty)
    {
        // Match legacy scripts or our exact CLI invocation, not arbitrary Aspire commands.
        if (node is not JsonObject hook)
        {
            return false;
        }
        var executable = hook[executableProperty] is JsonValue executableValue && executableValue.TryGetValue<string>(out var executableText)
            ? executableText : null;
        if (ReferencesTelemetryScript(executable))
        {
            return true;
        }
        if (hook["args"] is JsonArray args)
        {
            var values = args.Select(arg => arg is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).ToArray();
            var (currentCommand, _) = AgentTelemetryHook.GetCommand(AgentTelemetryProtocol.HookOptionName);
            var executableName = Path.GetFileNameWithoutExtension(executable);
            var isDotnet = string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase);
            var isDirectAspireInvocation = !isDotnet
                && values is [AgentTelemetryProtocol.AgentCommandName, AgentTelemetryProtocol.TelemetryCommandName, AgentTelemetryProtocol.HookOptionName]
                && (executable == currentCommand || string.Equals(executableName, "aspire", StringComparison.OrdinalIgnoreCase));
            var isManagedAspireInvocation = isDotnet
                && values is [var assemblyPath, AgentTelemetryProtocol.AgentCommandName, AgentTelemetryProtocol.TelemetryCommandName, AgentTelemetryProtocol.HookOptionName]
                && string.Equals(Path.GetFileName(assemblyPath), "aspire.dll", StringComparison.OrdinalIgnoreCase);

            return isDirectAspireInvocation || isManagedAspireInvocation || values.Any(ReferencesTelemetryScript);
        }

        return false;
    }

    internal static JsonArray CreateArgs(IEnumerable<string> args)
        => new(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray());

    private static bool ReferencesTelemetryScript(string? value)
        => value is not null
            && (value.Contains(TelemetryHookInstaller.ShellResourceName, StringComparison.OrdinalIgnoreCase)
                || value.Contains(TelemetryHookInstaller.PowerShellResourceName, StringComparison.OrdinalIgnoreCase));
}
