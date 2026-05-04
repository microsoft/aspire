// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Lifecycle;

/// <summary>
/// Resolves the path to the <c>aspire.terminalhost</c> binary on each
/// <see cref="TerminalHostResource"/> before DCP launches it. The resource is created
/// during <c>WithTerminal()</c> with a placeholder command because
/// <see cref="DcpOptions"/> is not yet configured at that point; this subscriber
/// finalises the executable command before <see cref="BeforeStartEvent"/> completes
/// and DCP picks the resource up.
/// </summary>
internal sealed class TerminalHostEventingSubscriber(
    IOptions<DcpOptions> dcpOptions,
    ILogger<TerminalHostEventingSubscriber> logger) : IDistributedApplicationEventingSubscriber
{
    private readonly IOptions<DcpOptions> _dcpOptions = dcpOptions ?? throw new ArgumentNullException(nameof(dcpOptions));
    private readonly ILogger<TerminalHostEventingSubscriber> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventing);
        eventing.Subscribe<BeforeStartEvent>(ResolveTerminalHostsAsync);
        return Task.CompletedTask;
    }

    private Task ResolveTerminalHostsAsync(BeforeStartEvent @event, CancellationToken cancellationToken)
    {
        var terminalHostPath = _dcpOptions.Value.TerminalHostPath;

        foreach (var host in @event.Model.Resources.OfType<TerminalHostResource>())
        {
            ValidateReplicaCount(host);

            if (host.Annotations.OfType<ExecutableAnnotation>().LastOrDefault() is not { } annotation)
            {
                continue;
            }

            if (annotation.Command != TerminalHostResource.UnresolvedCommand)
            {
                continue;
            }

            if (string.IsNullOrEmpty(terminalHostPath))
            {
                _logger.LogWarning(
                    "Terminal host binary path is not configured. The terminal for resource '{TargetName}' will not be available. Set ASPIRE_TERMINAL_HOST_PATH or ensure the Aspire SDK provides the 'aspireterminalhostpath' assembly metadata.",
                    host.Parent.Name);
                continue;
            }

            if (!File.Exists(terminalHostPath))
            {
                _logger.LogWarning(
                    "Terminal host binary not found at '{TerminalHostPath}'. The terminal for resource '{TargetName}' will not be available.",
                    terminalHostPath,
                    host.Parent.Name);
                continue;
            }

            annotation.Command = terminalHostPath;

            _logger.LogDebug(
                "Resolved terminal host '{HostName}' for target '{TargetName}' to '{TerminalHostPath}' with {ReplicaCount} replica(s).",
                host.Name,
                host.Parent.Name,
                terminalHostPath,
                host.Layout.ReplicaCount);
        }

        return Task.CompletedTask;
    }

    private void ValidateReplicaCount(TerminalHostResource host)
    {
        var declaredReplicas = host.Parent.Annotations.OfType<ReplicaAnnotation>().LastOrDefault()?.Replicas ?? 1;

        if (declaredReplicas != host.Layout.ReplicaCount)
        {
            _logger.LogWarning(
                "Terminal host for '{TargetName}' was sized for {LayoutReplicas} replica(s) at WithTerminal() time but the resource now declares {DeclaredReplicas}. Call WithReplicas(...) before WithTerminal() to avoid this. Only the first {LayoutReplicas} replica(s) will have an attachable terminal.",
                host.Parent.Name,
                host.Layout.ReplicaCount,
                declaredReplicas,
                host.Layout.ReplicaCount);
        }
    }
}
