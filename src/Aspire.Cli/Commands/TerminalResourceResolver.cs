// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Resolves resource replicas and AppHost terminal IDs for interactive and scripted terminal commands.
/// </summary>
internal sealed class TerminalResourceResolver(IInteractionService interactionService)
{
    public async Task<ResolvedTerminal?> ResolveAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        int? requestedReplica,
        CancellationToken cancellationToken)
    {
        var snapshots = await interactionService.ShowStatusAsync(
            TerminalCommandStrings.LookingUpResource,
            async () => await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false));

        var matches = ResourceSnapshotMapper.WhereMatchesResourceName(snapshots, resourceName).ToList();
        if (matches.Count == 0)
        {
            var terminalInfo = await connection.GetTerminalInfoAsync(resourceName, cancellationToken).ConfigureAwait(false);
            if (terminalInfo is { IsAvailable: true, AppHostTerminal: { } terminal })
            {
                if (requestedReplica is not null)
                {
                    interactionService.DisplayError(TerminalCommandStrings.AppHostReplicaNotSupported);
                    return null;
                }

                return new(terminal.TerminalId, terminal.Title, terminal.ConsumerUdsPath, null);
            }

            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                TerminalCommandStrings.ResourceNotFound, resourceName));
            return null;
        }

        // Replicas share the parent DisplayName carrying WithTerminal(), rather than their individual names.
        var canonicalName = !string.IsNullOrEmpty(matches[0].DisplayName)
            ? matches[0].DisplayName!
            : matches[0].Name;
        var info = await interactionService.ShowStatusAsync(
            TerminalCommandStrings.DiscoveringSessions,
            async () => await connection.GetTerminalInfoAsync(canonicalName, cancellationToken).ConfigureAwait(false));

        if (!info.IsAvailable || info.Replicas is not { Length: > 0 } replicas)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                TerminalCommandStrings.TerminalUnavailable, canonicalName));
            return null;
        }

        if (requestedReplica is { } index)
        {
            var match = Array.Find(replicas, r => r.ReplicaIndex == index);
            if (match is null)
            {
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                    TerminalCommandStrings.ReplicaNotFound, index, canonicalName,
                    string.Join(", ", replicas.Select(r => r.ReplicaIndex.ToString(CultureInfo.InvariantCulture)))));
            }
            return match is null ? null : FromReplica(canonicalName, match);
        }

        if (replicas.Length == 1)
        {
            return FromReplica(canonicalName, replicas[0]);
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                TerminalCommandStrings.ReplicaRequired, canonicalName, replicas.Length));
            return null;
        }

        var picked = await interactionService.PromptForSelectionAsync(
            string.Format(CultureInfo.CurrentCulture, TerminalCommandStrings.SelectReplica, canonicalName),
            replicas,
            r => r.IsAlive
                ? string.Format(CultureInfo.CurrentCulture, TerminalCommandStrings.ReplicaRunning, r.Label)
                : string.Format(CultureInfo.CurrentCulture, TerminalCommandStrings.ReplicaExited,
                    r.Label, r.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return FromReplica(canonicalName, picked);
    }

    private static ResolvedTerminal FromReplica(string name, TerminalReplicaInfo replica)
        => new(name, string.Format(CultureInfo.InvariantCulture, "{0} (replica {1})", name, replica.ReplicaIndex),
            replica.ConsumerUdsPath, replica);
}

internal sealed record ResolvedTerminal(string Name, string Label, string ConsumerUdsPath, TerminalReplicaInfo? Replica);
