// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Result of resolving an AppHost connection.
/// </summary>
internal sealed class AppHostConnectionResult
{
    public IAppHostAuxiliaryBackchannel? Connection { get; init; }

    [MemberNotNullWhen(true, nameof(Connection))]
    [MemberNotNullWhen(false, nameof(ErrorMessage))]
    public bool Success => Connection is not null;

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Discovers and resolves connections to running AppHosts when the socket path is not known.
/// Scans for running AppHosts and prompts the user to select one if multiple are found.
/// Used by CLI commands (stop, resources, logs, telemetry) that need to find a running AppHost.
/// For managing a specific instance when the socket path is known, use <see cref="Projects.RunningInstanceManager"/> instead.
/// </summary>
internal sealed class AppHostConnectionResolver(
    IAuxiliaryBackchannelMonitor backchannelMonitor,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    ILogger logger)
{
    /// <summary>
    /// Resolves all running AppHost connections using socket-first discovery.
    /// Used when stopping all running AppHosts (e.g., via --all flag).
    /// </summary>
    /// <param name="scanningMessage">Message to display while scanning for AppHosts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All resolved connections, or an empty array if none found.</returns>
    public async Task<AppHostConnectionResult[]> ResolveAllConnectionsAsync(
        string scanningMessage,
        CancellationToken cancellationToken)
    {
        var connections = await interactionService.ShowStatusAsync(
            scanningMessage,
            async () =>
            {
                await backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
                return backchannelMonitor.Connections.ToList();
            });

        if (connections.Count == 0)
        {
            return [];
        }

        return connections.Select(c => new AppHostConnectionResult { Connection = c }).ToArray();
    }

    /// <summary>
    /// Resolves an AppHost connection using socket-first discovery.
    /// </summary>
    /// <param name="projectFile">Optional project file. If specified, uses fast path to find matching socket.</param>
    /// <param name="scanningMessage">Message to display while scanning for AppHosts.</param>
    /// <param name="selectPrompt">Prompt to display when multiple AppHosts are found.</param>
    /// <param name="notFoundMessage">Message to display when no AppHosts are found.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved connection, or null with an error message.</returns>
    public async Task<AppHostConnectionResult> ResolveConnectionAsync(
        FileInfo? projectFile,
        string scanningMessage,
        string selectPrompt,
        string notFoundMessage,
        CancellationToken cancellationToken)
    {
        // Fast path: If --apphost was specified, check directly for its socket
        if (projectFile is not null)
        {
            var targetPath = projectFile.FullName;
            var matchingSockets = AppHostHelper.FindMatchingSockets(
                targetPath,
                executionContext.HomeDirectory.FullName);

            // Try each matching socket until we get a connection
            foreach (var socketPath in matchingSockets)
            {
                try
                {
                    var connection = await AppHostAuxiliaryBackchannel.ConnectAsync(
                        socketPath, logger, cancellationToken).ConfigureAwait(false);
                    if (connection is not null)
                    {
                        return new AppHostConnectionResult { Connection = connection };
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to connect to socket at {SocketPath}", socketPath);
                }
            }

            return new AppHostConnectionResult { ErrorMessage = notFoundMessage };
        }

        // Socket-first approach: Scan for running AppHosts via their sockets
        // This is fast because it only looks at ~/.aspire/backchannels/ directory
        // rather than recursively searching the entire directory tree for project files
        var connections = await interactionService.ShowStatusAsync(
            scanningMessage,
            async () =>
            {
                await backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
                return backchannelMonitor.Connections.ToList();
            });

        if (connections.Count == 0)
        {
            return new AppHostConnectionResult { ErrorMessage = notFoundMessage };
        }

        // Filter to in-scope AppHosts (within working directory)
        var workingDirectory = executionContext.WorkingDirectory.FullName;
        var inScopeConnections = connections.Where(c => c.IsInScope).ToList();
        var outOfScopeConnections = connections.Where(c => !c.IsInScope).ToList();

        IAppHostAuxiliaryBackchannel? selectedConnection = null;

        if (inScopeConnections.Count == 1)
        {
            // Only one in-scope AppHost, use it
            selectedConnection = inScopeConnections[0];
        }
        else if (inScopeConnections.Count > 1)
        {
            selectedConnection = await PromptForAppHostSelectionAsync(
                inScopeConnections,
                SharedCommandStrings.MultipleInScopeAppHosts,
                selectPrompt,
                path => Path.GetRelativePath(workingDirectory, path),
                cancellationToken);
        }
        else if (outOfScopeConnections.Count > 0)
        {
            selectedConnection = await PromptForAppHostSelectionAsync(
                outOfScopeConnections,
                SharedCommandStrings.NoInScopeAppHostsShowingAll,
                selectPrompt,
                path => path,
                cancellationToken);
        }

        if (selectedConnection is null)
        {
            return new AppHostConnectionResult { ErrorMessage = notFoundMessage };
        }

        return new AppHostConnectionResult { Connection = selectedConnection };
    }

    /// <summary>
    /// Displays an informational message, prompts the user to select from available AppHost connections,
    /// and displays the selected AppHost.
    /// </summary>
    private async Task<IAppHostAuxiliaryBackchannel?> PromptForAppHostSelectionAsync(
        List<IAppHostAuxiliaryBackchannel> candidateConnections,
        string contextMessage,
        string selectPrompt,
        Func<string, string> formatPath,
        CancellationToken cancellationToken)
    {
        interactionService.DisplayMessage(KnownEmojis.Information, contextMessage);

        // Order by most recently started first
        var choices = candidateConnections
            .OrderByDescending(c => c.AppHostInfo?.StartedAt ?? DateTimeOffset.MinValue)
            .Select(c =>
            {
                var appHostPath = c.AppHostInfo?.AppHostPath ?? "Unknown";
                return (Display: formatPath(appHostPath), Connection: c);
            })
            .ToList();

        var selectedDisplay = await interactionService.PromptForSelectionAsync(
            selectPrompt,
            choices.Select(c => c.Display).ToArray(),
            c => c.EscapeMarkup(),
            cancellationToken);

        var selectedConnection = choices.FirstOrDefault(c => c.Display == selectedDisplay).Connection;

        interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.UsingAppHost, selectedDisplay));

        return selectedConnection;
    }
}
