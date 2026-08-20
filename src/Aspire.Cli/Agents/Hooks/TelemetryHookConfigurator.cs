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
            // (https://docs.github.com/en/copilot/reference/hooks-reference) defines `bash` and
            // `powershell` as keys whose values are shell command strings. The `powershell` value
            // invokes `pwsh` (PowerShell 7+) because that is the documented Windows prerequisite for
            // Copilot CLI hooks.
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
                            ["powershell"] = HookCommandFormatter.BuildPwshCommand(scripts.PowerShellScriptPath),
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

        // Idempotent: drop any previously written Aspire entry before adding exactly one. This also
        // refreshes the command if the script path changed across CLI upgrades.
        RemoveExistingAspireEntries(postToolUse);

        // Claude Code runs a path-referencing hook best in exec form (`command` + `args`): the executable
        // is spawned directly with no shell, so the script path passes through verbatim with no quoting.
        // Shell form is avoided because on Windows Claude runs the command line through Git Bash (or
        // PowerShell only when Git Bash is absent), which would mismatch PowerShell-style path quoting. The
        // Claude hooks reference recommends exec form for any hook that references a script path; see the
        // "Exec form and shell form" / "Reference scripts by path" sections in
        // https://docs.claude.com/en/docs/claude-code/hooks.
        string command;
        JsonArray commandArgs;
        if (OperatingSystem.IsWindows())
        {
            // Use modern PowerShell 7+ (pwsh), consistent with the Copilot hook. pwsh is the documented
            // Windows prerequisite for agent hooks; if it is absent the hook simply does not run, the same
            // as Copilot. `-ExecutionPolicy Bypass` is passed straight to the process (exec form has no
            // shell) so the local script runs regardless of the machine policy; `-NoProfile` avoids profile
            // side effects and startup cost.
            command = "pwsh";
            commandArgs = new JsonArray("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scripts.PowerShellScriptPath);
        }
        else
        {
            command = "bash";
            commandArgs = new JsonArray(scripts.ShellScriptPath);
        }

        postToolUse.Add((JsonNode?)new JsonObject
        {
            ["matcher"] = "*",
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["args"] = commandArgs,
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
        // Match the distinctive script file name (track-telemetry.sh/.ps1) wherever a hook entry can
        // carry the path: an `args` element in exec form (the form we write), or embedded in the
        // `command` shell string in shell form. Both forms are valid in the hook schema, so checking
        // each keeps re-init idempotent regardless of which one an existing entry uses. Matching the
        // file name rather than just "aspire" avoids removing an unrelated user hook.
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
