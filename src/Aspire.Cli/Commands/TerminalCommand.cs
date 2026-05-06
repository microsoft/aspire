// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Hex1b;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Attaches the local terminal to an interactive PTY session for a resource that
/// was registered with <c>WithTerminal()</c>.
/// </summary>
/// <remarks>
/// The command:
/// 1. Resolves the running AppHost via <see cref="AppHostConnectionResolver"/>.
/// 2. Verifies the AppHost advertises the <c>terminals.v1</c> capability.
/// 3. Looks up the resource (by Name or DisplayName) and asks the AppHost for the
///    list of terminal replicas via <see cref="IAppHostAuxiliaryBackchannel.GetTerminalInfoAsync"/>.
/// 4. Picks a replica (auto if 1; <c>--replica N</c> if specified; interactive prompt
///    otherwise; errors in non-interactive contexts when no <c>--replica</c> is given).
/// 5. Hands the local console off to a Hex1b HMP v1 client connected to the chosen
///    replica's consumer UDS endpoint exposed by the hidden Aspire.TerminalHost.
/// </remarks>
internal sealed class TerminalCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<TerminalCommand> _logger;

    private static readonly Argument<string> s_resourceArgument = new("resource")
    {
        Description = "The name of the resource to attach a terminal to."
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption =
        new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    private static readonly Option<int?> s_replicaOption = new("--replica", "-r")
    {
        Description = "The 0-based replica index to attach to. Required when the resource has more than one replica and the CLI is not running interactively."
    };

    private static readonly Option<bool> s_viewerOption = new("--viewer")
    {
        Description = "Connect as a viewer (secondary) instead of taking primary control. Viewers see the terminal output but do not drive its dimensions. Useful when another peer (e.g., the dashboard) is currently driving the session."
    };

    public TerminalCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<TerminalCommand> logger)
        : base("terminal", "Attach to an interactive terminal session for a resource.", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _logger = logger;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_replicaOption);
        Options.Add(s_viewerOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var requestedReplica = parseResult.GetValue(s_replicaOption);
        var viewerOnly = parseResult.GetValue(s_viewerOption);

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            _interactionService.DisplayError("A resource name is required.");
            return ExitCodeConstants.InvalidCommand;
        }

        var connectionResult = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, "attach a terminal"),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!connectionResult.Success)
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, connectionResult.ErrorMessage);
            return ExitCodeConstants.Success;
        }

        var connection = connectionResult.Connection!;

        if (!connection.SupportsTerminalsV1)
        {
            _interactionService.DisplayError(
                "The connected AppHost does not support 'aspire terminal'. Update Aspire.Hosting to 13.4 or later.");
            return ExitCodeConstants.AppHostIncompatible;
        }

        var snapshots = await _interactionService.ShowStatusAsync(
            "Looking up resource...",
            async () => await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false));

        var matches = ResourceSnapshotMapper.WhereMatchesResourceName(snapshots, resourceName).ToList();
        if (matches.Count == 0)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                "Resource '{0}' was not found.", resourceName));
            return ExitCodeConstants.InvalidCommand;
        }

        // For replicated resources, all snapshots share the same DisplayName which
        // matches the parent resource name (the one carrying the TerminalAnnotation).
        // Fall back to Name for non-replicated resources where DisplayName is null/equal.
        var canonicalName = !string.IsNullOrEmpty(matches[0].DisplayName)
            ? matches[0].DisplayName!
            : matches[0].Name;

        var info = await _interactionService.ShowStatusAsync(
            "Discovering terminal sessions...",
            async () => await connection.GetTerminalInfoAsync(canonicalName, cancellationToken).ConfigureAwait(false));

        if (!info.IsAvailable || info.Replicas is null || info.Replicas.Length == 0)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                "Resource '{0}' is not available for terminal attachment. Make sure the resource was registered with '.WithTerminal()' and that the terminal host has started.",
                canonicalName));
            return ExitCodeConstants.InvalidCommand;
        }

        var (replica, selectionError) = await SelectReplicaAsync(info.Replicas, requestedReplica, canonicalName, cancellationToken).ConfigureAwait(false);
        if (selectionError != ExitCodeConstants.Success)
        {
            return selectionError;
        }
        Debug.Assert(replica is not null, "SelectReplicaAsync returns a non-null replica when error == Success.");

        if (!replica!.IsAlive)
        {
            _interactionService.DisplayMessage(KnownEmojis.Warning,
                string.Format(CultureInfo.CurrentCulture,
                    "Replica {0} of '{1}' has exited (code {2}). Attaching to the historical buffer; no live input will be sent.",
                    replica.ReplicaIndex,
                    canonicalName,
                    replica.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"));
        }

        _interactionService.DisplayMessage(KnownEmojis.Information,
            string.Format(CultureInfo.CurrentCulture,
                "Attaching to '{0}' replica {1}. Press Ctrl+C to detach.",
                canonicalName,
                replica.ReplicaIndex));

        try
        {
            // Phase 11: Multi-head HMP1 wire-up.
            //
            // We always announce ourselves as "aspire-cli" and pass a defaultRole
            // hint reflecting the user's intent ("viewer" with --viewer, otherwise
            // "interactive"). The server uses the hint as roster metadata only — it
            // does not auto-grant primary status. To actually drive the PTY's
            // dimensions, an interactive client must call RequestPrimaryAsync.
            //
            // For backward compatibility with single-head deployments (no other
            // peer connected), the default behavior is to take primary on connect.
            // Pass --viewer to attach without disturbing the current primary.
            var defaultRole = viewerOnly ? "viewer" : "interactive";

            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHmp1Client(
                    ct => Hmp1Transports.ConnectUnixSocket(replica.ConsumerUdsPath, ct),
                    displayName: "aspire-cli",
                    defaultRole: defaultRole)
                .Build();

            var hmp1 = terminal.Hmp1!;

            hmp1.RoleChanged += (_, e) =>
            {
                _logger.LogDebug(
                    "Multi-head RoleChanged: primary={PrimaryPeerId} dims={Width}x{Height} reason={Reason} previously={Previously} now={Now}",
                    e.PrimaryPeerId,
                    e.Width,
                    e.Height,
                    e.Reason,
                    e.PreviouslyPrimary,
                    e.NowPrimary);
            };

            hmp1.PeerJoined += (_, e) =>
            {
                _logger.LogDebug("Multi-head PeerJoined: peerId={PeerId} displayName={DisplayName}", e.PeerId, e.DisplayName);
            };

            hmp1.PeerLeft += (_, e) =>
            {
                _logger.LogDebug("Multi-head PeerLeft: peerId={PeerId}", e.PeerId);
            };

            // When not in viewer mode, kick off a background task that requests
            // primary as soon as the handshake completes. We poll for PeerId
            // because Hmp1WorkloadAdapter does not expose a "Connected" event.
            // The window is small (typically <50ms) and the request is cheap.
            if (!viewerOnly)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (string.IsNullOrEmpty(hmp1.PeerId) && !cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                        }
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        var (cols, rows) = TryGetLocalDimensions();
                        await hmp1.RequestPrimaryAsync(cols, rows, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Multi-head RequestPrimary failed; remaining as secondary.");
                    }
                }, cancellationToken);
            }

            return await terminal.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodeConstants.Success;
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Failed to connect to terminal at {Path}", replica.ConsumerUdsPath);
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                "Could not connect to terminal session for '{0}' (replica {1}). Is the AppHost still running?",
                canonicalName, replica.ReplicaIndex));
            return ExitCodeConstants.FailedToExecuteResourceCommand;
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            _logger.LogDebug(ex, "Terminal session connection lost at {Path}", replica.ConsumerUdsPath);
            _interactionService.DisplayMessage(KnownEmojis.Information,
                string.Format(CultureInfo.CurrentCulture,
                    "Terminal session for '{0}' (replica {1}) ended.",
                    canonicalName, replica.ReplicaIndex));
            return ExitCodeConstants.Success;
        }
    }

    private async Task<(TerminalReplicaInfo? Replica, int ErrorExitCode)> SelectReplicaAsync(
        TerminalReplicaInfo[] replicas,
        int? requestedReplica,
        string canonicalName,
        CancellationToken cancellationToken)
    {
        if (requestedReplica.HasValue)
        {
            var match = Array.Find(replicas, r => r.ReplicaIndex == requestedReplica.Value);
            if (match is null)
            {
                _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                    "Replica index {0} is not available for resource '{1}'. Available indices: {2}.",
                    requestedReplica.Value,
                    canonicalName,
                    string.Join(", ", replicas.Select(r => r.ReplicaIndex.ToString(CultureInfo.InvariantCulture)))));
                return (null, ExitCodeConstants.InvalidCommand);
            }
            return (match, ExitCodeConstants.Success);
        }

        if (replicas.Length == 1)
        {
            return (replicas[0], ExitCodeConstants.Success);
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                "Resource '{0}' has {1} replicas. Pass --replica <index> to choose one in non-interactive mode.",
                canonicalName,
                replicas.Length));
            return (null, ExitCodeConstants.InvalidCommand);
        }

        var picked = await _interactionService.PromptForSelectionAsync(
            string.Format(CultureInfo.CurrentCulture, "Select a replica of '{0}' to attach to:", canonicalName),
            replicas,
            r => r.IsAlive
                ? string.Format(CultureInfo.CurrentCulture, "{0} (running)", r.Label)
                : string.Format(CultureInfo.CurrentCulture, "{0} (exited code={1})", r.Label, r.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
            cancellationToken).ConfigureAwait(false);

        return (picked, ExitCodeConstants.Success);
    }

    private static (int Cols, int Rows) TryGetLocalDimensions()
    {
        // Prefer the live console size when available. Fall back to the producer's
        // default 80x24 if the CLI is being invoked in a non-console context — in
        // that case the request still succeeds and the producer keeps its current
        // size if both dimensions match.
        try
        {
            var cols = Console.WindowWidth;
            var rows = Console.WindowHeight;
            if (cols > 0 && rows > 0)
            {
                return (cols, rows);
            }
        }
        catch (IOException)
        {
        }
        return (80, 24);
    }
}
