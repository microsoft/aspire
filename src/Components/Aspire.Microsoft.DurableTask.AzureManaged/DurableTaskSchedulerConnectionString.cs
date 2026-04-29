// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Microsoft.DurableTask.AzureManaged;

/// <summary>
/// Utility for parsing Durable Task Scheduler connection strings.
/// </summary>
internal static class DurableTaskSchedulerConnectionString
{
    /// <summary>
    /// Extracts the <c>Endpoint</c> value from a Durable Task Scheduler connection string.
    /// </summary>
    public static string? GetEndpoint(string connectionString)
    {
        return TryGetValue(connectionString, "Endpoint", out var value) ? value : null;
    }

    /// <summary>
    /// Extracts the <c>TaskHub</c> value from a Durable Task Scheduler connection string.
    /// </summary>
    public static string? GetTaskHubName(string connectionString)
    {
        return TryGetValue(connectionString, "TaskHub", out var value) ? value : null;
    }

    private static bool TryGetValue(string connectionString, string key, out string? value)
    {
        value = null;

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = part.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var partKey = part.AsSpan(0, equalsIndex).Trim();
            if (partKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = part[(equalsIndex + 1)..].Trim();
                return true;
            }
        }

        return false;
    }
}
