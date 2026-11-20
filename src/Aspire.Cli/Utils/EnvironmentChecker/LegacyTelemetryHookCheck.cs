// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal sealed class LegacyTelemetryHookCheck(
    IEnumerable<IAgentClient> clients,
    ILogger<LegacyTelemetryHookCheck> logger) : IEnvironmentCheck
{
    internal static string GetCheckName(string clientId) => $"agent-telemetry-hook-{clientId}";

    public int Order => 100;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<EnvironmentCheckResult>();

        // Only inspect user-level registrations that agent init owns and can refresh.
        foreach (var client in clients.Where(c => c.SupportsTelemetryHooks))
        {
            var path = client.GetTelemetryHookConfigPath();
            bool isRegistered;
            try
            {
                isRegistered = await LegacyTelemetryHook.IsRegisteredAsync(path, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A malformed file for one client must not hide a legacy hook for another client.
                logger.LogDebug(ex, "Could not inspect telemetry hook configuration at {Path}.", path);
                continue;
            }

            if (isRegistered)
            {
                results.Add(new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Environment,
                    Name = GetCheckName(client.Id),
                    Status = EnvironmentCheckStatus.Warning,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.LegacyTelemetryHookMessageFormat, client.DisplayName),
                    Fix = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.LegacyTelemetryHookFixFormat, client.Id),
                    Details = path,
                });
            }
        }

        return results;
    }
}
