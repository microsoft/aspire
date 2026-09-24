// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Copilot;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Default <see cref="ITelemetryHookConfigurator"/>. Refreshes compatibility scripts and writes the native
/// <c>PostToolUse</c> hook into each supported client's <b>user-level</b> configuration.
/// </summary>
/// <remarks>
/// Only user-level configuration is ever written. The GitHub Copilot CLI hooks reference confirms that
/// Copilot reads cross-tool <c>.claude/settings.json</c> only at the repository level (never <c>~/.claude</c>),
/// so the Copilot user hook (<c>~/.copilot/hooks/aspire-telemetry.json</c>) and the Claude user hook
/// (<c>~/.claude/settings.json</c>) cannot both fire for the same event — the hook is registered exactly
/// once per client by construction.
/// See https://docs.github.com/en/copilot/reference/hooks-reference.
/// </remarks>
internal sealed class TelemetryHookConfigurator : ITelemetryHookConfigurator
{
    private const string CopilotHooksDirectoryName = "hooks";
    private const string CopilotHookFileName = "aspire-telemetry.json";

    private const string ClaudeFolderName = ".claude";
    private const string ClaudeSettingsFileName = "settings.json";
    private const string ClaudePostToolUseKey = "PostToolUse";

    private const int HookTimeoutSeconds = 30;

    private readonly ITelemetryHookInstaller _installer;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<TelemetryHookConfigurator> _logger;

    public TelemetryHookConfigurator(
        ITelemetryHookInstaller installer,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<TelemetryHookConfigurator> logger)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _installer = installer;
        _executionContext = executionContext;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TelemetryHookConfigurationResult> ConfigureAsync(
        IReadOnlyCollection<AgentClientKind> detectedClients,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detectedClients);

        var configured = new List<AgentClientKind>();
        var skipped = new List<TelemetryHookSkip>();

        // VS Code and OpenCode hook schemas are not yet verified, so they are intentionally not
        // configured here even though they are detected/marked. The Copilot App and CLI share the
        // same ~/.copilot hook location, so prefer the App display name when both are detected.
        // This identifies the configuration target, not the hook event's client-name: the canonical
        // scripts currently report App events as copilot-cli. See https://github.com/microsoft/aspire-skills/issues/71.
        var supported = new List<AgentClientKind>();
        if (detectedClients.Contains(AgentClientKind.CopilotApp))
        {
            supported.Add(AgentClientKind.CopilotApp);
        }
        else if (detectedClients.Contains(AgentClientKind.CopilotCli))
        {
            supported.Add(AgentClientKind.CopilotCli);
        }

        if (detectedClients.Contains(AgentClientKind.ClaudeCode))
        {
            supported.Add(AgentClientKind.ClaudeCode);
        }

        if (supported.Count == 0)
        {
            return new TelemetryHookConfigurationResult(configured, skipped);
        }

        // Keep compatibility scripts current for existing registrations and older clients.
        await _installer.EnsureInstalledAsync(cancellationToken);

        foreach (var client in supported)
        {
            switch (client)
            {
                case AgentClientKind.CopilotApp:
                case AgentClientKind.CopilotCli:
                    if (await TryConfigureCopilotAsync(cancellationToken))
                    {
                        configured.Add(client);
                    }
                    else
                    {
                        skipped.Add(new TelemetryHookSkip(client, TelemetryHookSkipReason.WriteFailed));
                    }
                    break;

                case AgentClientKind.ClaudeCode:
                    var claudeSkipReason = await ConfigureClaudeAsync(cancellationToken);
                    if (claudeSkipReason is { } reason)
                    {
                        skipped.Add(new TelemetryHookSkip(client, reason));
                    }
                    else
                    {
                        configured.Add(client);
                    }
                    break;
            }
        }

        return new TelemetryHookConfigurationResult(configured, skipped);
    }

    private async Task<bool> TryConfigureCopilotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hooksDirectory = ResolveCopilotHooksDirectory();
            Directory.CreateDirectory(hooksDirectory);

            var filePath = Path.Combine(hooksDirectory, CopilotHookFileName);

            // Direct exec avoids shell cold starts on every tool call while keeping all-event coverage.
            // https://docs.github.com/en/copilot/reference/hooks-reference#command-hooks
            var (command, args) = AgentTelemetryHook.GetCommand("--hook");
            var config = new JsonObject
            {
                ["version"] = 1,
                ["hooks"] = new JsonObject
                {
                    ["postToolUse"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["exec"] = command,
                            ["args"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
                            ["timeoutSec"] = HookTimeoutSeconds,
                        }),
                },
            };

            await WriteJsonAtomicAsync(filePath, config, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to write Copilot CLI telemetry hook configuration.");
            return false;
        }
    }

    private async Task<TelemetryHookSkipReason?> ConfigureClaudeAsync(CancellationToken cancellationToken)
    {
        var claudeDirectory = Path.Combine(_executionContext.HomeDirectory.FullName, ClaudeFolderName);
        var settingsPath = Path.Combine(claudeDirectory, ClaudeSettingsFileName);

        JsonObject settings;
        if (File.Exists(settingsPath))
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(settingsPath, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Failed to read Claude settings at {Path}.", settingsPath);
                return TelemetryHookSkipReason.WriteFailed;
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(content);
            }
            catch (JsonException ex)
            {
                // Never clobber a file we can't understand; leave it untouched and report the skip.
                _logger.LogDebug(ex, "Claude settings at {Path} contained malformed JSON; skipping hook registration.", settingsPath);
                return TelemetryHookSkipReason.MalformedConfig;
            }

            switch (parsed)
            {
                // An empty file or a literal `null` document: start from a fresh object.
                case null:
                    settings = new JsonObject();
                    break;
                case JsonObject existing:
                    settings = existing;
                    break;
                // Root is valid JSON but not an object (array/string/number/bool): another tool owns this
                // file in a shape we don't recognize. AsObject() would throw InvalidOperationException (not
                // JsonException), escape the best-effort callers, and crash `agent init`. Skip instead.
                default:
                    return TelemetryHookSkipReason.UnexpectedConfigShape;
            }
        }
        else
        {
            settings = new JsonObject();
        }

        // `hooks` and its `PostToolUse` child must have the documented shapes; an unexpected shape means
        // another tool owns the file, so skip rather than risk corrupting it.
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

        JsonArray postToolUse;
        if (hooks.TryGetPropertyValue(ClaudePostToolUseKey, out var postToolUseNode))
        {
            if (postToolUseNode is not JsonArray postToolUseArray)
            {
                return TelemetryHookSkipReason.UnexpectedConfigShape;
            }

            postToolUse = postToolUseArray;
        }
        else
        {
            postToolUse = new JsonArray();
            hooks[ClaudePostToolUseKey] = postToolUse;
        }

        // Idempotent: replace both legacy scripts and native Aspire entries, preserving user hooks.
        RemoveExistingAspireEntries(postToolUse);

        // Keep exec form and wildcard coverage, but run the CLI directly instead of a shell.
        // https://docs.claude.com/en/docs/claude-code/hooks.
        var (command, args) = AgentTelemetryHook.GetCommand("--hook");

        postToolUse.Add((JsonNode?)new JsonObject
        {
            ["matcher"] = "*",
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["args"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
                    ["timeout"] = HookTimeoutSeconds,
                }),
        });

        try
        {
            Directory.CreateDirectory(claudeDirectory);
            await WriteJsonAtomicAsync(settingsPath, settings, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to write Claude settings at {Path}.", settingsPath);
            return TelemetryHookSkipReason.WriteFailed;
        }
    }

    private string ResolveCopilotHooksDirectory()
    {
        // The Copilot CLI hooks reference resolves the user-level hooks directory from COPILOT_HOME when
        // set, otherwise ~/.copilot/hooks. Mirror that so the hook lands where Copilot actually reads it.
        var configDirectory = CopilotPaths.GetConfigDirectory(_executionContext.HomeDirectory, _environment);
        return Path.Combine(configDirectory, CopilotHooksDirectoryName);
    }

    private static void RemoveExistingAspireEntries(JsonArray postToolUse)
    {
        // Iterate in reverse so removals don't shift indices we still need to visit. Remove individual
        // Aspire hook entries (not whole groups) so a user-authored hook sharing a matcher group survives,
        // then drop any group left empty by that removal.
        for (var groupIndex = postToolUse.Count - 1; groupIndex >= 0; groupIndex--)
        {
            if (postToolUse[groupIndex] is not JsonObject group
                || !group.TryGetPropertyValue("hooks", out var innerNode)
                || innerNode is not JsonArray innerHooks)
            {
                continue;
            }

            for (var hookIndex = innerHooks.Count - 1; hookIndex >= 0; hookIndex--)
            {
                if (IsAspireHook(innerHooks[hookIndex]))
                {
                    innerHooks.RemoveAt(hookIndex);
                }
            }

            if (innerHooks.Count == 0)
            {
                postToolUse.RemoveAt(groupIndex);
            }
        }
    }

    private static bool IsAspireHook(JsonNode? node)
    {
        // Recognize legacy scripts and our exact native command, not arbitrary Aspire commands.
        if (node is not JsonObject hook)
        {
            return false;
        }

        if (hook.TryGetPropertyValue("command", out var commandNode)
            && commandNode is JsonValue commandValue
            && commandValue.TryGetValue<string>(out var command)
            && ReferencesTelemetryScript(command))
        {
            return true;
        }

        if (hook.TryGetPropertyValue("args", out var argsNode) && argsNode is JsonArray args)
        {
            var values = args.Select(arg => arg is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).ToArray();
            var executable = hook["command"] is JsonValue executableValue && executableValue.TryGetValue<string>(out var executableText)
                ? executableText : null;
            var (currentCommand, _) = AgentTelemetryHook.GetCommand("--hook");
            var executableName = Path.GetFileNameWithoutExtension(executable);
            var isDotnet = string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase);
            if ((values is ["agent", "telemetry", "--hook"] && !isDotnet
                    && (executable == currentCommand || string.Equals(executableName, "aspire", StringComparison.OrdinalIgnoreCase)))
                || (values is [var assemblyPath, "agent", "telemetry", "--hook"] && isDotnet
                    && string.Equals(Path.GetFileName(assemblyPath), "aspire.dll", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            foreach (var arg in args)
            {
                if (arg is JsonValue argValue
                    && argValue.TryGetValue<string>(out var argString)
                    && ReferencesTelemetryScript(argString))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ReferencesTelemetryScript(string? value)
        => value is not null
            && (value.Contains("track-telemetry.sh", StringComparison.OrdinalIgnoreCase)
                || value.Contains("track-telemetry.ps1", StringComparison.OrdinalIgnoreCase));

    private static async Task WriteJsonAtomicAsync(string path, JsonObject config, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(config, JsonSourceGenerationContext.Default.JsonObject);

        // Write to a sibling temp file then move into place so a concurrently firing hook never reads a
        // half-written config.
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}
