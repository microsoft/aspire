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
    IEnumerable<IAgentEnvironmentScanner> environmentScanners,
    CliExecutionContext executionContext,
    ILogger<TelemetryHookConfigurator> logger) : ITelemetryHookConfigurator
{
    internal const int HookTimeoutSeconds = 30;

    public IEnumerable<AgentConfigurationTarget> Plan(AgentInitRequest request)
    {
        if (request.Scope is not AgentConfigurationScope.User || !request.Assets.HasAssets || request.Environments.Count == 0)
        {
            yield break;
        }

        Task<TelemetryHookScripts>? installation = null;
        // User-scoped setup instruments detected clients independently of the selected
        // assets. Project-only setup must not register or repair files in the user's home.
        // Selecting an undetected client must not create its hook.
        foreach (var scanner in environmentScanners)
        {
            if (scanner.GetHookConfiguration(request) is { } configuration)
            {
                yield return Target(scanner, configuration);
            }
        }

        AgentConfigurationTarget Target(IAgentEnvironmentScanner scanner, AgentHookConfiguration configuration)
            => new(configuration.Path, AgentConfigurationScope.User, AgentAssetKind.TelemetryHooks, [scanner], "hooks:aspire",
                async (root, context, cancellationToken) =>
                {
                    var settings = await AgentConfigurationJson.ReadSettingsAsync(context, configuration.PolicyPaths, cancellationToken);
                    if (settings.Append(root).Any(config =>
                        AgentConfigurationJson.Boolean(config, "disableAllHooks") is true ||
                        AgentConfigurationJson.Boolean(config, "allowManagedHooksOnly") is true))
                    {
                        return AgentConfigurationEdit.Skipped(AgentCommandStrings.Configuration_PolicyBlocked);
                    }

                    var hasProjectHook = false;
                    foreach (var configPath in configuration.ExistingHookPaths)
                    {
                        // A project/user alias can point at the same physical Claude file.
                        if (AgentPath.Comparer.Equals(AgentPath.Resolve(configPath), AgentPath.Resolve(configuration.Path)))
                        {
                            continue;
                        }

                        if (await context.ReadOptionalAsync(configPath, cancellationToken) is { } config &&
                            ContainsAspireHook(config))
                        {
                            hasProjectHook = true;
                            break;
                        }
                    }

                    if (!hasProjectHook)
                    {
                        configuration.Validate(root);
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

                    // Existing project hooks still need their embedded scripts repaired or
                    // refreshed after a CLI upgrade, even though no user hook should be added.
                    if (hasProjectHook)
                    {
                        return AgentConfigurationEdit.Skipped(AgentCommandStrings.Configuration_ExistingProjectHook);
                    }

                    configuration.Apply(root, scripts, IsAspireHook);

                    return AgentConfigurationEdit.Applied();
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
