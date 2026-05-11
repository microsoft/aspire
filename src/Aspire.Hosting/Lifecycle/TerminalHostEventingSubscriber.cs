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
/// <see cref="TerminalHostResource"/> before DCP launches it. Each resource is created
/// during <c>WithTerminal()</c> with a placeholder command because
/// <see cref="DcpOptions"/> is not yet configured at that point; this subscriber
/// finalises the executable command before <see cref="BeforeStartEvent"/> completes
/// and DCP picks the resources up.
/// </summary>
/// <remarks>
/// Each parent replica gets its own <see cref="TerminalHostResource"/>, so this iterates
/// over all of them and resolves each independently. They all point at the same binary
/// (just with different per-replica UDS args).
/// </remarks>
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
        var invocationArgs = ParseInvocationArgs(_dcpOptions.Value.TerminalHostInvocationArgs);

        // Surface a one-time warning per parent if the AppHost replica count drifted from
        // what TerminalAnnotation.TerminalHosts.Count was sized for at WithTerminal() time.
        // De-duplicated by parent name so a 5-replica drift doesn't log the same warning
        // 5 times.
        var warnedParents = new HashSet<string>(StringComparers.ResourceName);

        foreach (var host in @event.Model.Resources.OfType<TerminalHostResource>())
        {
            ValidateReplicaIndex(host, warnedParents);

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
                    "Terminal host binary path is not configured. The terminal for resource '{TargetName}' (replica {ReplicaIndex}) will not be available. Set ASPIRE_TERMINAL_HOST_PATH or ensure the Aspire SDK provides the 'aspireterminalhostpath' assembly metadata.",
                    host.Parent.Name, host.ParentReplicaIndex);
                continue;
            }

            if (!File.Exists(terminalHostPath))
            {
                _logger.LogWarning(
                    "Terminal host binary not found at '{TerminalHostPath}'. The terminal for resource '{TargetName}' (replica {ReplicaIndex}) will not be available.",
                    terminalHostPath,
                    host.Parent.Name, host.ParentReplicaIndex);
                continue;
            }

            annotation.Command = terminalHostPath;

            if (invocationArgs.Length > 0)
            {
                // Prepend the invocation args (e.g. "terminalhost") so the multi-mode
                // aspire-managed.exe dispatches to TerminalHostApp.RunAsync. Mirrors how
                // the Dashboard wires "dashboard" via DashboardEventHandlers.
                host.Annotations.Add(new CommandLineArgsCallbackAnnotation(args =>
                {
                    for (var i = 0; i < invocationArgs.Length; i++)
                    {
                        args.Insert(i, invocationArgs[i]);
                    }
                }));
            }

            _logger.LogDebug(
                "Resolved terminal host '{HostName}' for target '{TargetName}' replica {ReplicaIndex} to '{TerminalHostPath}' (invocation args: '{InvocationArgs}').",
                host.Name,
                host.Parent.Name,
                host.ParentReplicaIndex,
                terminalHostPath,
                _dcpOptions.Value.TerminalHostInvocationArgs ?? string.Empty);
        }

        return Task.CompletedTask;
    }

    private static string[] ParseInvocationArgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private void ValidateReplicaIndex(TerminalHostResource host, HashSet<string> warnedParents)
    {
        var declaredReplicas = host.Parent.Annotations.OfType<ReplicaAnnotation>().LastOrDefault()?.Replicas ?? 1;

        // ParentReplicaIndex was assigned at WithTerminal() time. If WithReplicas() was
        // called afterwards and increased the count, replicas without a matching host get
        // no terminal. If the count decreased, hosts at the tail will never see a producer.
        // Either way we want the same warning the previous design surfaced.
        if (host.ParentReplicaIndex >= declaredReplicas && warnedParents.Add(host.Parent.Name))
        {
            _logger.LogWarning(
                "Terminal host(s) for '{TargetName}' were sized at WithTerminal() time but the resource now declares {DeclaredReplicas} replica(s). Call WithReplicas(...) before WithTerminal() to avoid this. Replicas without a matching terminal host will run without an attachable terminal.",
                host.Parent.Name,
                declaredReplicas);
        }
    }
}
