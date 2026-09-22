// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Contributes one user-level hook per detected supported client, sharing Copilot App/CLI targets.
/// </summary>
internal sealed class TelemetryHookConfigurator(
    ITelemetryHookInstaller installer,
    CliExecutionContext executionContext,
    IEnvironment environment,
    ILogger<TelemetryHookConfigurator> logger) : ITelemetryHookConfigurator
{
    internal const int HookTimeoutSeconds = 30;

    public IEnumerable<AgentConfigurationTarget> Plan(AgentInitRequest request)
    {
        if (!request.Assets.HasAssets || request.Clients.Count == 0)
        {
            yield break;
        }

        Task<TelemetryHookScripts>? installation = null;
        // Hooks instrument detected clients independently of the assets and native client
        // targets selected for setup. Selecting an undetected client must not create its hook.
        var detectedClients = request.Detections.Select(detection => detection.Client).Distinct().ToArray();
        var copilotClients = detectedClients.Where(client => client.Environment is CopilotAgentEnvironmentScanner).ToArray();
        if (copilotClients.Length > 0)
        {
            yield return Target(Path.Combine(CopilotPaths.GetConfigDirectory(executionContext, environment), "hooks", "aspire-telemetry.json"), copilotClients, copilot: true);
        }

        if (detectedClients.SingleOrDefault(client => client.Environment is ClaudeCodeAgentEnvironmentScanner) is { } claude)
        {
            yield return Target(Path.Combine(ClaudeCodeAgentEnvironmentScanner.GetConfigDirectory(executionContext, environment), "settings.json"), [claude], copilot: false);
        }

        // These usage scripts support Copilot and Claude, not standalone VS Code/OpenCode
        // hook configuration. Plugin-format compatibility does not imply identical hook contracts.
        AgentConfigurationTarget Target(string path, IReadOnlyList<AgentClient> clients, bool copilot)
            => new(path, AgentConfigurationScope.User, AgentAssetKind.TelemetryHooks, clients, "hooks:aspire",
                async (root, context, cancellationToken) =>
                {
                    var settingsPaths = copilot
                        ? CopilotPaths.PluginSettings(request, executionContext, environment)
                        : ClaudeCodeAgentEnvironmentScanner.PluginSettings(request, executionContext, environment);
                    var settings = await AgentConfigurationJson.ReadSettingsAsync(context, settingsPaths, cancellationToken);
                    if (settings.Append(root).Any(config =>
                        AgentConfigurationJson.Boolean(config, "disableAllHooks") is true ||
                        AgentConfigurationJson.Boolean(config, "allowManagedHooksOnly") is true))
                    {
                        return AgentConfigurationEdit.Skipped(AgentCommandStrings.Configuration_PolicyBlocked);
                    }

                    var projectSettings = copilot
                        ? CopilotPaths.ExistingHookSettings(request, executionContext, environment)
                        : ClaudeCodeAgentEnvironmentScanner.ProjectSettings(request.WorkspaceRoot);

                    foreach (var configPath in projectSettings)
                    {
                        // A project/user alias can point at the same physical Claude file.
                        if (AgentPath.Comparer.Equals(AgentPath.Resolve(configPath), AgentPath.Resolve(path)))
                        {
                            continue;
                        }

                        if (await context.ReadOptionalAsync(configPath, cancellationToken) is { } config &&
                            ContainsAspireHook(config))
                        {
                            return AgentConfigurationEdit.Skipped(AgentCommandStrings.Configuration_ExistingProjectHook);
                        }
                    }

                    if (copilot)
                    {
                        CopilotAgentEnvironmentScanner.ValidateHooks(root);
                    }
                    else
                    {
                        ClaudeCodeAgentEnvironmentScanner.ValidateHooks(root);
                    }

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
                            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_HookInstallationFailed, ex.Message));
                    }

                    if (copilot)
                    {
                        CopilotAgentEnvironmentScanner.ApplyHook(root, scripts, IsAspireHook);
                    }
                    else
                    {
                        ClaudeCodeAgentEnvironmentScanner.ApplyHook(root, scripts, IsAspireHook);
                    }

                    return AgentConfigurationEdit.Applied(AgentCommandStrings.Configuration_HookConfigured);
                });
    }

    private bool ContainsAspireHook(JsonObject root)
    {
        if (AgentConfigurationJson.OptionalObject(root, "hooks") is not { } hooks)
        {
            return false;
        }

        foreach (var key in new[] { ClaudeCodeAgentEnvironmentScanner.HookEventName, CopilotAgentEnvironmentScanner.HookEventName })
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
