// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Contributes one user-level hook per supported native client, sharing Copilot App/CLI targets.
/// </summary>
internal sealed class TelemetryHookConfigurator(
    ITelemetryHookInstaller installer,
    CliExecutionContext executionContext,
    AgentConfigurationPaths paths,
    ILogger<TelemetryHookConfigurator> logger) : ITelemetryHookConfigurator
{
    private const int HookTimeoutSeconds = 30;

    public IEnumerable<AgentConfigurationTarget> Plan(AgentInitRequest request)
    {
        if (!request.Assets.AspireSkills && !request.Assets.Mcp)
        {
            yield break;
        }

        Task<TelemetryHookScripts>? installation = null;
        var copilotClients = request.Clients.Where(client => client is AgentClientKind.CopilotCli or AgentClientKind.CopilotApp).Distinct().ToArray();
        if (copilotClients.Length > 0)
        {
            yield return Target(Path.Combine(paths.CopilotDirectory, "hooks", "aspire-telemetry.json"), copilotClients, copilot: true);
        }

        if (request.Clients.Contains(AgentClientKind.ClaudeCode))
        {
            yield return Target(Path.Combine(paths.ClaudeDirectory, "settings.json"), [AgentClientKind.ClaudeCode], copilot: false);
        }

        // No VS Code/OpenCode hook schemas are invented. VS Code's supported Copilot-backed
        // runtime shares plugin registration, which is distinct from a verified hook target.
        AgentConfigurationTarget Target(string path, IReadOnlyList<AgentClientKind> clients, bool copilot)
            => new(path, AgentConfigurationScope.User, AgentAssetKind.TelemetryHooks, clients, "hooks:aspire",
                async (root, context, cancellationToken) =>
                {
                    if (!clients.Any(context.HasAspireConfiguration))
                    {
                        return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.HookNotApplicable);
                    }

                    var settings = await AgentConfigurationJson.ReadSettingsAsync(context, paths.PluginSettings(request, copilot), cancellationToken);
                    if (settings.Append(root).Any(config =>
                        AgentConfigurationJson.Boolean(config, "disableAllHooks") is true ||
                        AgentConfigurationJson.Boolean(config, "allowManagedHooksOnly") is true))
                    {
                        return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.PolicyBlocked);
                    }

                    // Copilot also reads Claude's repository hooks, never Claude's user
                    // settings. Avoid adding a second event source to a known project hook.
                    // https://docs.github.com/en/copilot/reference/hooks-reference
                    var projectSettings = new[]
                    {
                        Path.Combine(request.WorkspaceRoot.FullName, ".claude", "settings.json"),
                        Path.Combine(request.WorkspaceRoot.FullName, ".claude", "settings.local.json")
                    }.ToList();
                    if (copilot)
                    {
                        projectSettings.Add(Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.json"));
                        projectSettings.Add(Path.Combine(request.WorkspaceRoot.FullName, ".github", "copilot", "settings.local.json"));
                        projectSettings.Add(Path.Combine(request.WorkspaceRoot.FullName, ".github", "hooks", "aspire-telemetry.json"));
                        projectSettings.Add(Path.Combine(paths.CopilotDirectory, "settings.json"));
                    }

                    foreach (var configPath in projectSettings)
                    {
                        // A project/user alias can point at the same physical Claude file.
                        if (AgentConfigurationPath.Comparer.Equals(AgentConfigurationPath.Resolve(configPath), AgentConfigurationPath.Resolve(path)))
                        {
                            continue;
                        }

                        if (await context.ReadOptionalAsync(configPath, cancellationToken) is { } config &&
                            ContainsAspireHook(config))
                        {
                            return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.ExistingProjectHook);
                        }
                    }

                    ValidateHooks(root, copilot);
                    TelemetryHookScripts scripts;
                    try
                    {
                        // The lazily materialized scripts are shared by every target in this
                        // setup invocation, including failures. Cancellation is never caught.
                        scripts = await (installation ??= installer.EnsureInstalledAsync(cancellationToken));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        logger.LogDebug(ex, "Could not install the embedded Aspire telemetry hooks.");
                        return new AgentConfigurationEdit(AgentConfigurationStatus.Failed,
                            string.Format(CultureInfo.CurrentCulture, AgentConfigurationStrings.HookInstallationFailed, ex.Message));
                    }

                    if (copilot)
                    {
                        ApplyCopilot(root, scripts);
                    }
                    else
                    {
                        ApplyClaude(root, scripts);
                    }

                    return AgentConfigurationEdit.Applied(AgentConfigurationStrings.HookConfigured);
                });
    }

    private static void ValidateHooks(JsonObject root, bool copilot)
    {
        if (copilot && root.TryGetPropertyValue("version", out var version) &&
            (version is not JsonValue number || !number.TryGetValue<int>(out var value) || value != 1))
        {
            throw AgentConfigurationJson.Shape("version");
        }

        var hooks = AgentConfigurationJson.OptionalObject(root, "hooks");
        var key = copilot ? "postToolUse" : "PostToolUse";
        if (hooks is null || !hooks.TryGetPropertyValue(key, out var entries))
        {
            return;
        }

        if (entries is not JsonArray array || array.Any(entry => entry is not JsonObject))
        {
            throw AgentConfigurationJson.Shape($"hooks.{key}");
        }

        if (!copilot)
        {
            foreach (var group in array.OfType<JsonObject>())
            {
                if (group["hooks"] is not JsonArray inner || inner.Any(entry => entry is not JsonObject) ||
                    (group.ContainsKey("matcher") && AgentConfigurationJson.String(group["matcher"]) is null))
                {
                    throw AgentConfigurationJson.Shape($"hooks.{key}");
                }
            }
        }
    }

    private void ApplyCopilot(JsonObject root, TelemetryHookScripts scripts)
    {
        root["version"] = 1;
        var hooks = AgentConfigurationJson.Object(root, "hooks");
        var entries = hooks["postToolUse"] as JsonArray ?? new JsonArray();
        if (!hooks.ContainsKey("postToolUse"))
        {
            hooks["postToolUse"] = entries;
        }

        var desired = new JsonObject
        {
            ["type"] = "command",
            ["bash"] = HookCommandFormatter.BuildBashCommand(scripts.ShellScriptPath),
            ["powershell"] = HookCommandFormatter.BuildPwshCommand(scripts.PowerShellScriptPath),
            ["timeoutSec"] = HookTimeoutSeconds
        };
        var owned = entries.Where(IsAspireHook).ToArray();
        if (owned.Length == 1 && JsonNode.DeepEquals(owned[0], desired))
        {
            return;
        }

        foreach (var entry in owned)
        {
            entries.Remove(entry);
        }

        entries.Add((JsonNode)desired);
    }

    private void ApplyClaude(JsonObject root, TelemetryHookScripts scripts)
    {
        var hooks = AgentConfigurationJson.Object(root, "hooks");
        var groups = hooks["PostToolUse"] as JsonArray ?? new JsonArray();
        if (!hooks.ContainsKey("PostToolUse"))
        {
            hooks["PostToolUse"] = groups;
        }

        // Retain the established exec form and shipped script/event/opt-out contract.
        // https://code.claude.com/docs/en/hooks#command-hook-fields
        var desired = new JsonObject
        {
            ["type"] = "command",
            ["command"] = OperatingSystem.IsWindows() ? "pwsh" : "bash",
            ["args"] = OperatingSystem.IsWindows()
                ? new JsonArray("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scripts.PowerShellScriptPath)
                : new JsonArray(scripts.ShellScriptPath),
            ["timeout"] = HookTimeoutSeconds
        };

        var owned = groups.OfType<JsonObject>()
            .SelectMany(group => ((JsonArray)group["hooks"]!).Where(IsAspireHook).Select(hook => (Group: group, Hook: hook)))
            .ToArray();
        if (owned.Length == 1 && AgentConfigurationJson.String(owned[0].Group["matcher"]) == "*" &&
            JsonNode.DeepEquals(owned[0].Hook, desired))
        {
            return;
        }

        foreach (var (group, hook) in owned)
        {
            var entries = (JsonArray)group["hooks"]!;
            entries.Remove(hook);
            if (entries.Count == 0)
            {
                groups.Remove(group);
            }
        }

        groups.Add((JsonNode)new JsonObject { ["matcher"] = "*", ["hooks"] = new JsonArray(desired) });
    }

    private bool ContainsAspireHook(JsonObject root)
    {
        if (AgentConfigurationJson.OptionalObject(root, "hooks") is not { } hooks)
        {
            return false;
        }

        foreach (var key in new[] { "PostToolUse", "postToolUse" })
        {
            if (hooks[key] is JsonArray entries && entries.Any(entry => IsAspireHook(entry) ||
                (entry is JsonObject group && group["hooks"] is JsonArray inner && inner.Any(IsAspireHook))))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAspireHook(JsonNode? node)
        => node is JsonObject hook &&
            (ReferencesAspireScript(AgentConfigurationJson.String(hook["command"])) ||
             ReferencesAspireScript(AgentConfigurationJson.String(hook["bash"])) ||
             ReferencesAspireScript(AgentConfigurationJson.String(hook["powershell"])) ||
             (hook["args"] is JsonArray args && args.Any(value => ReferencesAspireScript(AgentConfigurationJson.String(value)))));

    private bool ReferencesAspireScript(string? value)
    {
        if (value is null)
        {
            return false;
        }

        // Shell commands contain quoted paths, for example bash '/home/o'\''brien/.aspire/hooks/track-telemetry.sh'.
        // Match Aspire's directory as well as the filename: other products also ship track-telemetry scripts.
        var normalized = value.Replace("'\\''", "'").Replace("''", "'").Replace('\\', '/');
        var current = Path.Combine(executionContext.AspireHomeDirectory.FullName, "hooks").Replace('\\', '/');
        return new[] { "track-telemetry.sh", "track-telemetry.ps1" }.Any(name =>
            normalized.Contains($"{current}/{name}", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains($"/.aspire/hooks/{name}", StringComparison.OrdinalIgnoreCase));
    }
}
