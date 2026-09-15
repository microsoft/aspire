// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using Aspire.Dashboard.Model;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stamps the per-replica terminal properties (<c>terminal.enabled</c>,
/// <c>terminal.replicaIndex</c>, <c>terminal.replicaCount</c>, and the sensitive
/// <c>terminal.consumerUdsPath</c>) onto a resource snapshot when the resource opted into
/// <see cref="TerminalResourceBuilderExtensions.WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/>.
/// </summary>
/// <remarks>
/// Both the dashboard gRPC path (<c>DashboardServiceData</c>) and the auxiliary backchannel
/// path (<c>AuxiliaryBackchannelRpcTarget</c>, which feeds <c>aspire describe</c> and the VS Code
/// extension) need the exact same set of terminal properties so consumers can detect terminal
/// availability and target the right replica. Sharing this helper keeps the two emission paths
/// from drifting: previously only the dashboard stamped these properties, so <c>aspire describe</c>
/// never surfaced <c>terminal.enabled</c> and the extension's "Open terminal" action stayed hidden.
/// </remarks>
internal static class TerminalResourceSnapshotProperties
{
    /// <summary>
    /// Returns <paramref name="properties"/> augmented with the terminal properties when
    /// <paramref name="resource"/> carries a <see cref="TerminalAnnotation"/>; otherwise returns
    /// <paramref name="properties"/> unchanged.
    /// </summary>
    /// <param name="resource">The resource whose snapshot is being built.</param>
    /// <param name="resourceId">The per-replica DCP resource id (e.g. <c>myapp-abc123</c>) used to
    /// resolve the stable 0-based replica index.</param>
    /// <param name="properties">The existing snapshot properties to augment.</param>
    public static ImmutableArray<ResourcePropertySnapshot> AddTerminalProperties(
        IResource resource,
        string resourceId,
        ImmutableArray<ResourcePropertySnapshot> properties)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(resourceId);

        var terminalAnnotation = resource.Annotations.OfType<TerminalAnnotation>().FirstOrDefault();
        if (terminalAnnotation is null)
        {
            return properties;
        }

        var terminalHosts = terminalAnnotation.TerminalHosts;
        var replicaCount = terminalHosts.Count;
        var replicaIndex = ResolveReplicaIndex(resource, resourceId);
        var consumerUdsPath = (uint)replicaIndex < (uint)replicaCount
            ? terminalHosts[replicaIndex].Layout.ConsumerUdsPath
            : null;

        properties = properties
            .Add(new ResourcePropertySnapshot(KnownProperties.Terminal.Enabled, "true") { IsSensitive = false })
            .Add(new ResourcePropertySnapshot(KnownProperties.Terminal.ReplicaIndex, replicaIndex.ToString(CultureInfo.InvariantCulture)) { IsSensitive = false })
            .Add(new ResourcePropertySnapshot(KnownProperties.Terminal.ReplicaCount, replicaCount.ToString(CultureInfo.InvariantCulture)) { IsSensitive = false });

        if (consumerUdsPath is not null)
        {
            // Mark the UDS path sensitive so the dashboard masks it in the resource details panel.
            // The backchannel path redacts sensitive values to null before they reach the CLI, so
            // the consumer socket path is never exposed to `aspire describe` / the extension; the CLI
            // resolves the real path through GetTerminalInfoAsync instead.
            properties = properties.Add(
                new ResourcePropertySnapshot(KnownProperties.Terminal.ConsumerUdsPath, consumerUdsPath) { IsSensitive = true });
        }

        return properties;
    }

    // Maps the per-replica DCP resourceId (e.g. "myapp-abc123") back to its stable 0-based replica
    // index by consulting DcpInstancesAnnotation, which DcpNameGenerator populates at
    // instance-allocation time. Falls back to 0 for non-DCP resources or when the annotation isn't
    // present yet (e.g. initial pre-DCP snapshots).
    private static int ResolveReplicaIndex(IResource resource, string resourceId)
    {
        var instances = resource.Annotations.OfType<DcpInstancesAnnotation>().FirstOrDefault();
        if (instances is null)
        {
            return 0;
        }

        foreach (var instance in instances.Instances)
        {
            if (string.Equals(instance.Name, resourceId, StringComparison.Ordinal))
            {
                return instance.Index;
            }
        }

        return 0;
    }
}
