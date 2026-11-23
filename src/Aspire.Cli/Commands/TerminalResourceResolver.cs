// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Dashboard.Model;

namespace Aspire.Cli.Commands;

/// <summary>
/// Resolves a resource name or instance ID to exactly one terminal endpoint.
/// </summary>
internal sealed class TerminalResourceResolver(IInteractionService interactionService)
{
    public async Task<(string ResourceName, TerminalReplicaInfo? Replica)> ResolveAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        int? requestedReplica,
        CancellationToken cancellationToken)
    {
        var snapshots = await interactionService.ShowStatusAsync(
            TerminalCommandStrings.LookingUpResource,
            async () => await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false));

        var matches = ResourceSnapshotMapper.ResolveResources(resourceName, snapshots);
        if (matches.Count == 0)
        {
            // Keep ambiguous display-name matches for diagnostics and legacy numeric selection,
            // without allowing them to override the shared resolver's exact instance-name match.
            matches = ResourceSnapshotMapper.WhereMatchesResourceName(snapshots, resourceName).ToList();
        }

        if (matches.Count == 0)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                TerminalCommandStrings.ResourceNotFound, resourceName));
            return (resourceName, null);
        }

        if (matches.Count > 1 && requestedReplica is null)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                TerminalCommandStrings.ResourceAmbiguous, resourceName,
                string.Join(", ", matches.Select(s => s.Name).Order(StringComparers.ResourceName))));
            return (resourceName, null);
        }

        var snapshot = matches[0];
        // Discovery still takes the parent model name. Keep the instance snapshot to map
        // its terminal.replicaIndex to an endpoint instead of losing the selected identity.
        var canonicalName = !string.IsNullOrEmpty(snapshot.DisplayName)
            ? snapshot.DisplayName
            : snapshot.Name;
        var info = await interactionService.ShowStatusAsync(
            TerminalCommandStrings.DiscoveringSessions,
            async () => await connection.GetTerminalInfoAsync(canonicalName, cancellationToken).ConfigureAwait(false));

        if (!info.IsAvailable || info.Replicas is not { Length: > 0 } replicas)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                TerminalCommandStrings.TerminalUnavailable, canonicalName));
            return (canonicalName, null);
        }

        // Preserve explicit numeric selection for callers using the hidden legacy option.
        if (requestedReplica is { } requestedIndex)
        {
            var match = Array.Find(replicas, r => r.ReplicaIndex == requestedIndex);
            if (match is null)
            {
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                    TerminalCommandStrings.ReplicaNotFound, requestedIndex, canonicalName,
                    string.Join(", ", replicas.Select(r => r.ReplicaIndex.ToString(CultureInfo.InvariantCulture)))));
            }

            return (canonicalName, match);
        }

        if (replicas.Length > 1 && string.Equals(resourceName, canonicalName, StringComparisons.ResourceName))
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                TerminalCommandStrings.ResourceInstanceRequired, resourceName));
            return (canonicalName, null);
        }

        // Backchannel snapshots stamp the index as a JSON string, e.g. "terminal.replicaIndex": "1".
        // Labels such as "shell-1" are not the DCP instance ID ("shell-abc123").
        if (snapshot.Properties.TryGetValue(KnownProperties.Terminal.ReplicaIndex, out var property))
        {
            if (property is JsonValue value &&
                value.TryGetValue<string>(out var indexText) &&
                int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                Array.Find(replicas, r => r.ReplicaIndex == index) is { } replica)
            {
                return (canonicalName, replica);
            }
        }
        else if (replicas.Length == 1 &&
            ResourceSnapshotMapper.WhereMatchesResourceName(snapshots, canonicalName).Count() == 1)
        {
            return (canonicalName, replicas[0]);
        }

        interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
            TerminalCommandStrings.InstanceTerminalUnavailable, snapshot.Name));
        return (canonicalName, null);
    }
}
