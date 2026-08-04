// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Lifecycle;

/// <summary>
/// Observes <see cref="TerminalHostResource"/> state transitions and, when one transitions
/// into a terminal failure state, surfaces the failure to the user by:
/// <list type="number">
///   <item>Unhiding the (normally hidden) host resource in the dashboard / <c>aspire ps</c> so
///         the failure is discoverable instead of buried under the parent's "WaitUntilStarted"
///         hang.</item>
///   <item>Writing a diagnostic line into the host's resource log stream — the place the user
///         naturally lands when they click the unhidden resource — telling them what likely
///         went wrong and how to recover.</item>
/// </list>
/// <para>
/// The most common failure mode this surfaces is a CLI / AppHost version mismatch: a user upgrades
/// their AppHost to a version that calls <c>.WithTerminal()</c> but has an older bundled
/// <c>aspire-managed</c> binary on PATH (resolved via the DashboardPath inference in
/// <see cref="DcpOptions"/>). The old binary rejects the <c>terminalhost</c> subcommand and exits
/// non-zero. Before this service existed, the only symptom was the parent resource hanging on
/// <c>WaitUntilStarted</c> against a hidden, silently-failing dependency.
/// </para>
/// </summary>
internal sealed class TerminalHostFailureDiagnosticService(
    ResourceNotificationService notifications,
    ResourceLoggerService loggers,
    IOptions<DcpOptions> dcpOptions,
    ILogger<TerminalHostFailureDiagnosticService> logger) : BackgroundService
{
    // States we treat as terminal-failure for a terminal host. Finished/Exited with non-zero
    // exit code or no exit code (e.g. host crashed before reporting) is a failure; with
    // exit code 0 it is a clean shutdown during AppHost stop and must not be surfaced as
    // a failure (otherwise every successful run would log a phantom error on app shutdown).
    private static readonly string[] s_terminalStates =
    [
        KnownResourceStates.FailedToStart,
        KnownResourceStates.Finished,
        KnownResourceStates.Exited,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // DCP can re-emit terminal-state events during recycle attempts and shutdown. Track
        // which hosts we've already diagnosed (by resource id, which is the per-replica DCP
        // name) so we only write the diagnostic block once per host instance.
        var diagnosedHosts = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await foreach (var evt in notifications.WatchAsync(stoppingToken).ConfigureAwait(false))
            {
                if (evt.Resource is not TerminalHostResource host)
                {
                    continue;
                }

                var state = evt.Snapshot.State?.Text;
                if (state is null || Array.IndexOf(s_terminalStates, state) < 0)
                {
                    continue;
                }

                // Treat exit code 0 (or "Finished"/"Exited" + zero) as a clean stop, not a
                // failure. FailedToStart is always a failure regardless of exit code.
                if (state != KnownResourceStates.FailedToStart
                    && evt.Snapshot.ExitCode is 0)
                {
                    continue;
                }

                if (!diagnosedHosts.Add(evt.ResourceId))
                {
                    continue;
                }

                await SurfaceFailureAsync(host, evt.Snapshot, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Diagnostic services must never bring down the host. Log and exit.
            logger.LogDebug(ex, "TerminalHostFailureDiagnosticService stopped unexpectedly.");
        }
    }

    private async Task SurfaceFailureAsync(TerminalHostResource host, CustomResourceSnapshot snapshot, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // Unhide the failed host so it shows up in the dashboard resource list and aspire ps.
        // Hosts are hidden by default (see ConfigureTerminalHostAnnotations) because they are
        // an implementation detail of WithTerminal(); on failure we make them discoverable so
        // the user has a place to land that explains the failure.
        try
        {
            await notifications.PublishUpdateAsync(host, s => s with { IsHidden = false })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to unhide terminal host '{HostName}' after failure.", host.Name);
        }

        var resourceLogger = loggers.GetLogger(host);

        var exitInfo = snapshot.ExitCode is { } code
            ? $" (exit code {code.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            : string.Empty;

        var binaryPath = dcpOptions.Value.TerminalHostPath ?? "<unresolved>";

        resourceLogger.LogError(
            "Terminal host for '{TargetName}' (replica {ReplicaIndex}) failed to start{ExitInfo}. Binary: '{TerminalHostPath}'.",
            host.Parent.Name,
            host.ParentReplicaIndex,
            exitInfo,
            binaryPath);

        if (LooksLikeBundledCliHost(dcpOptions.Value.TerminalHostPath, dcpOptions.Value.TerminalHostInvocationArgs))
        {
            // We resolved the host to the bundled aspire-managed multi-mode binary and asked it
            // to dispatch via the "terminalhost" subcommand. Pre-13.4 aspire-managed builds only
            // accept dashboard|server|nuget, so this combination most commonly fails when an
            // older bundled aspire CLI is paired with an AppHost that calls .WithTerminal().
            resourceLogger.LogError(
                "If you recently added .WithTerminal() to a resource, the bundled aspire CLI on this machine may be older than this AppHost and may not ship the 'terminalhost' command. Run `aspire update --self` to upgrade the CLI, then re-run the AppHost. If the issue persists after upgrading, please open an issue at https://aka.ms/aspire/new-issue.");
        }
    }

    private static bool LooksLikeBundledCliHost(string? path, string? invocationArgs)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(invocationArgs))
        {
            return false;
        }

        // The bundled-CLI path is uniquely identified by the combination of "aspire-managed"
        // as the binary name and "terminalhost" as the leading invocation arg (the dispatcher
        // subcommand). Per-RID NuGet terminal-host packages (historical, pre-bundle-cutover)
        // shipped their own dedicated binary without a leading subcommand arg, so they would
        // not match.
        return invocationArgs.Trim().Equals("terminalhost", StringComparison.Ordinal)
            && BundleDiscovery.IsAspireManagedBinary(path);
    }
}
