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
/// <see cref="TerminalHostResource"/> before DCP launches it. The hosts themselves are
/// created earlier in the same <see cref="BeforeStartEvent"/> by the subscription
/// installed by <see cref="TerminalResourceBuilderExtensions.WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/>;
/// they each carry a placeholder <see cref="TerminalHostResource.UnresolvedCommand"/> because
/// <see cref="DcpOptions"/> is not yet configured at <c>WithTerminal()</c> time. This
/// subscriber finalises the executable command before the event completes and DCP picks
/// the resources up.
/// </summary>
/// <remarks>
/// <para>
/// Ordering note: builder-phase event subscriptions (registered by <c>WithTerminal()</c>)
/// fire ahead of DI-registered <see cref="IDistributedApplicationEventingSubscriber"/>
/// instances, so by the time this subscriber runs the per-replica hosts are already in
/// <c>@event.Model.Resources</c>.
/// </para>
/// <para>
/// Each parent replica gets its own <see cref="TerminalHostResource"/>, so this iterates
/// over all of them and resolves each independently. They all point at the same binary
/// (just with different per-replica UDS args).
/// </para>
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

        // No need to validate ParentReplicaIndex against the parent's ReplicaAnnotation:
        // WithTerminal() now defers host creation to BeforeStartEvent and reads the final
        // replica count at that point, so by construction the count of hosts always equals
        // the parent's declared replica count.
        foreach (var host in @event.Model.Resources.OfType<TerminalHostResource>())
        {
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
}
