// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.ConsoleLogs;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Fetches console logs from the dashboard client and converts them to log entries.
/// </summary>
public sealed class ConsoleLogsFetcher
{
    private readonly IDashboardClient _dashboardClient;
    private readonly ConsoleLogsManager _consoleLogsManager;

    public bool IsEnabled => _dashboardClient.IsEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleLogsFetcher"/> class.
    /// </summary>
    /// <param name="dashboardClient">The dashboard client.</param>
    /// <param name="consoleLogsManager">The console logs manager.</param>
    public ConsoleLogsFetcher(IDashboardClient dashboardClient, ConsoleLogsManager consoleLogsManager)
    {
        _dashboardClient = dashboardClient;
        _consoleLogsManager = consoleLogsManager;
    }

    private async Task<List<LogEntry>> FetchLogEntriesAsync(string resourceName, DateTime? filterDate, CancellationToken cancellationToken)
    {
        var logEntries = new List<LogEntry>();
        var logParser = new LogParser(ConsoleColor.Black);

        await foreach (var batch in _dashboardClient.GetConsoleLogs(resourceName, cancellationToken).ConfigureAwait(false))
        {
            foreach (var logLine in batch)
            {
                var logEntry = logParser.CreateLogEntry(logLine.Content, logLine.IsErrorMessage, resourcePrefix: null);

                // Apply filter if specified
                if (filterDate is not null && logEntry.Timestamp is not null && logEntry.Timestamp <= filterDate)
                {
                    continue;
                }

                logEntries.Add(logEntry);
            }
        }

        return logEntries;
    }

    /// <summary>
    /// Fetches console logs for all resources and converts them to log entries.
    /// </summary>
    public async Task<Dictionary<string, List<LogEntry>>> FetchLogEntriesAsync(HashSet<string> resourceNames, CancellationToken cancellationToken)
    {
        if (!_dashboardClient.IsEnabled)
        {
            throw new InvalidOperationException("Can't fetch console logs when the dashboard client is not enabled.");
        }

        var resources = _dashboardClient.GetResources().Where(r => resourceNames.Contains(r.Name)).ToList();
        var result = new Dictionary<string, List<LogEntry>>(StringComparer.OrdinalIgnoreCase);

        // Fetch logs for all resources in parallel
        var logTasks = resources.Select(async resource =>
        {
            var filterDate = _consoleLogsManager.GetFilterDate(resource.Name);
            var logEntries = await FetchLogEntriesAsync(resource.Name, filterDate, cancellationToken).ConfigureAwait(false);
            var resourceName = ResourceViewModel.GetResourceName(resource, resources);
            return (ResourceName: resourceName, LogEntries: logEntries);
        });

        var results = await Task.WhenAll(logTasks).ConfigureAwait(false);

        foreach (var (resourceName, logEntries) in results)
        {
            if (logEntries.Count > 0)
            {
                result[resourceName] = logEntries;
            }
        }

        return result;
    }
}
