// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tui;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Attaches the local terminal to an interactive PTY session for a resource that
/// was registered with <c>WithTerminal()</c>.
/// </summary>
/// <remarks>
/// The command:
/// <list type="number">
/// <item>Resolves the running AppHost via <see cref="AppHostConnectionResolver"/>.</item>
/// <item>Verifies the AppHost advertises the <c>terminals.v1</c> capability.</item>
/// <item>Looks up the resource (by Name or DisplayName) and asks the AppHost for the
///   list of terminal replicas via <see cref="IAppHostAuxiliaryBackchannel.GetTerminalInfoAsync"/>.</item>
/// <item>Picks a replica (auto if 1; <c>--replica N</c> if specified; interactive prompt
///   otherwise; errors in non-interactive contexts when no <c>--replica</c> is given).</item>
/// <item>Hands the local console off to <see cref="TerminalViewerApp"/>, which owns the
///   embedded HMP v1 wire-up and the role-aware InfoBar TUI.</item>
/// </list>
/// </remarks>
internal sealed class TerminalAttachCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly TerminalResourceResolver _terminalResolver;
    private readonly ILogger<TerminalAttachCommand> _logger;

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

    public TerminalAttachCommand(
        AppHostConnectionResolver connectionResolver,
        TerminalResourceResolver terminalResolver,
        ILogger<TerminalAttachCommand> logger,
        CommonCommandServices services)
        : base("attach", "Attach the local terminal to an interactive PTY session for a resource.", services)
    {
        _interactionService = services.InteractionService;
        _logger = logger;
        _connectionResolver = connectionResolver;
        _terminalResolver = terminalResolver;

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_replicaOption);
        Options.Add(s_viewerOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var requestedReplica = parseResult.GetValue(s_replicaOption);
        var viewerOnly = parseResult.GetValue(s_viewerOption);

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            _interactionService.DisplayError("A resource name is required.");
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        var connectionResult = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, "attach a terminal"),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!connectionResult.Success)
        {
            return CommandResult.FromExitCode(AppHostConnectionResultHandler.DisplayFailureAsInformation(connectionResult, _interactionService));
        }

        var connection = connectionResult.Connection!;

        if (!connection.SupportsTerminalsV1)
        {
            _interactionService.DisplayError(
                "The connected AppHost does not support 'aspire terminal'. Update Aspire.Hosting to 13.4 or later.");
            return CommandResult.Failure(CliExitCodes.AppHostIncompatible);
        }

        var (canonicalName, replica) = await _terminalResolver.ResolveAsync(
            connection, resourceName, requestedReplica, cancellationToken).ConfigureAwait(false);
        if (replica is null)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        if (!replica.IsAlive)
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
                "Attaching to '{0}' replica {1}. Press Ctrl+B D to detach, Ctrl+B T to take control.",
                canonicalName,
                replica.ReplicaIndex));

        try
        {
            // Delegate the embedded HMP1 wire-up and role-aware TUI shell to
            // TerminalViewerApp. It owns the Hex1bTerminal builder (via the
            // WithHmp1UdsClient extension) and renders an InfoBar with role /
            // peers / dims plus a tmux-style chord hotkey set:
            //
            //   Ctrl+B D  → detach (clean exit)
            //   Ctrl+B T  → take control (request primary)
            //
            // When --viewer is passed, the app connects as secondary and stays
            // passive until the user explicitly hits Ctrl+B T. Otherwise it
            // auto-takes primary on connect (preserving the single-head default
            // behaviour) and the "Take" InfoBar slot disappears in favour of
            // "(primary)".
            var sessionLabel = string.Format(CultureInfo.InvariantCulture,
                "{0} (replica {1})", canonicalName, replica.ReplicaIndex);
            var displayName = string.Format(CultureInfo.InvariantCulture,
                "aspire-cli:{0}", Environment.ProcessId);
            var viewerApp = new TerminalViewerApp(replica.ConsumerUdsPath, sessionLabel, displayName, viewerOnly, _logger);
            return CommandResult.FromExitCode(await viewerApp.RunAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Success();
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Failed to connect to terminal at {Path}", replica.ConsumerUdsPath);
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                "Could not connect to terminal session for '{0}' (replica {1}). Is the AppHost still running?",
                canonicalName, replica.ReplicaIndex));
            return CommandResult.Failure(CliExitCodes.FailedToExecuteResourceCommand);
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            _logger.LogDebug(ex, "Terminal session connection lost at {Path}", replica.ConsumerUdsPath);
            _interactionService.DisplayMessage(KnownEmojis.Information,
                string.Format(CultureInfo.CurrentCulture,
                    "Terminal session for '{0}' (replica {1}) ended.",
                    canonicalName, replica.ReplicaIndex));
            return CommandResult.Success();
        }
    }
}
