// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Default <see cref="ITelemetryHookConfigurator"/>. Materializes the hook scripts once and writes the
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
    private const string CopilotFolderName = ".copilot";
    private const string CopilotHooksDirectoryName = "hooks";
    private const string CopilotHookFileName = "aspire-telemetry.json";
    private const string CopilotHomeEnvironmentVariable = "COPILOT_HOME";

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
        // configured here even though they are detected/marked. Only configure once per client kind.
        var supported = detectedClients
            .Where(static c => c is AgentClientKind.CopilotCli or AgentClientKind.ClaudeCode)
            .Distinct()
            .ToList();

        if (supported.Count == 0)
        {
            return new TelemetryHookConfigurationResult(configured, skipped);
        }

        // Materialize the scripts once; every supported client references the same absolute paths.
        var scripts = await _installer.EnsureInstalledAsync(cancellationToken);

        foreach (var client in supported)
        {
            switch (client)
            {
                case AgentClientKind.CopilotCli:
                    if (await TryConfigureCopilotAsync(scripts, cancellationToken))
                    {
                        configured.Add(client);
                    }
                    else
                    {
                        skipped.Add(new TelemetryHookSkip(client, TelemetryHookSkipReason.WriteFailed));
                    }
                    break;

                case AgentClientKind.ClaudeCode:
                    var claudeSkipReason = await ConfigureClaudeAsync(scripts, cancellationToken);
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

    private async Task<bool> TryConfigureCopilotAsync(TelemetryHookScripts scripts, CancellationToken cancellationToken)
    {
        try
        {
            var hooksDirectory = ResolveCopilotHooksDirectory();
            Directory.CreateDirectory(hooksDirectory);

            var filePath = Path.Combine(hooksDirectory, CopilotHookFileName);

            // Owned file: a full overwrite is trivially idempotent. The Copilot CLI hooks reference
            // (https://docs.github.com/en/copilot/reference/hooks-reference) defines `bash`/`powershell`
            // as shell command strings, so we pass `bash '<sh>'` / `powershell ... -File '<ps1>'`.
            var config = new JsonObject
            {
                ["version"] = 1,
                ["hooks"] = new JsonObject
                {
                    ["postToolUse"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["bash"] = HookCommandFormatter.BuildBashCommand(scripts.ShellScriptPath),
                            ["powershell"] = HookCommandFormatter.BuildPowerShellCommand(scripts.PowerShellScriptPath),
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

    private async Task<TelemetryHookSkipReason?> ConfigureClaudeAsync(TelemetryHookScripts scripts, CancellationToken cancellationToken)
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

            try
            {
                settings = JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                // Never clobber a file we can't understand; leave it untouched and report the skip.
                _logger.LogDebug(ex, "Claude settings at {Path} contained malformed JSON; skipping hook registration.", settingsPath);
                return TelemetryHookSkipReason.MalformedConfig;
            }
        }
        else
        {
            settings = new JsonObject();
        }

        // The `hooks` value and its `PostToolUse` child must have the documented shapes. An unexpected
        // shape means another tool owns this file in a way we don't recognize, so we skip rather than risk
        // corrupting it.
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

        // Idempotent: drop any previously written Aspire entry before adding exactly one. This also
        // refreshes the command if the script path changed across CLI upgrades.
        RemoveExistingAspireEntries(postToolUse);

        // On Windows run the .ps1, otherwise run the .sh under bash. The path is single-quoted, which
        // preserves spaces in both bash and PowerShell. A literal apostrophe in the home path is the one
        // case the two shells quote differently; that is rare enough on Windows to accept.
        var command = OperatingSystem.IsWindows()
            ? HookCommandFormatter.BuildPowerShellCommand(scripts.PowerShellScriptPath)
            : HookCommandFormatter.BuildBashCommand(scripts.ShellScriptPath);

        postToolUse.Add((JsonNode?)new JsonObject
        {
            ["matcher"] = "*",
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    // Bound the hook so a stuck telemetry call can never stall a Claude session. The shell
                    // scripts also self-limit, but Claude's own timeout is the reliable backstop.
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
        var copilotHome = _environment.GetEnvironmentVariable(CopilotHomeEnvironmentVariable);
        if (!string.IsNullOrEmpty(copilotHome))
        {
            return Path.Combine(copilotHome, CopilotHooksDirectoryName);
        }

        return Path.Combine(_executionContext.HomeDirectory.FullName, CopilotFolderName, CopilotHooksDirectoryName);
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
        // Our command embeds the materialized script path, which always ends in the distinctive
        // file names track-telemetry.sh / track-telemetry.ps1. Matching on those file names (rather
        // than just "aspire") avoids removing an unrelated user hook, while still catching a stale
        // entry that points at a previous script location after the home directory moved.
        if (node is not JsonObject hook
            || !hook.TryGetPropertyValue("command", out var commandNode)
            || commandNode is not JsonValue commandValue
            || !commandValue.TryGetValue<string>(out var command)
            || command is null)
        {
            return false;
        }

        return command.Contains("track-telemetry.sh", StringComparison.OrdinalIgnoreCase)
            || command.Contains("track-telemetry.ps1", StringComparison.OrdinalIgnoreCase);
    }

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
