// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Lists every terminal in the connected AppHost — those belonging to <c>WithTerminal</c>-enabled
/// resources, and those the AppHost itself owns (dock tabs, interaction dialogs, automation-only
/// sessions). Backs <c>aspire terminal ps</c>.
/// </summary>
/// <remarks>
/// The command:
/// <list type="number">
/// <item>Resolves the running AppHost via <see cref="AppHostConnectionResolver"/>.</item>
/// <item>Verifies the AppHost advertises the <c>terminals.v1</c> capability and falls back to a
///   "not supported" error message when it does not (older AppHosts pre-13.4 lack the entire
///   WithTerminal/<c>terminal attach</c>+<c>terminal ps</c> surface).</item>
/// <item>Calls <see cref="IAppHostAuxiliaryBackchannel.ListTerminalsAsync"/> to enumerate them.
///   Resources whose host process isn't reachable are still listed with a status indicating they
///   are unavailable rather than silently dropped.</item>
/// <item>Renders the result as Spectre.Console tables or — when <c>--format json</c> is supplied
///   — a flat JSON document for scripting.</item>
/// </list>
/// </remarks>
internal sealed class TerminalPsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<TerminalPsCommand> _logger;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption =
        new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = "Output format. 'text' (default) renders a table; 'json' emits structured output for scripting."
    };

    private static readonly Option<bool> s_verboseOption = new("--verbose", "-v")
    {
        Description = "Include per-peer details for every attached HMP1 viewer (peer id, display name)."
    };

    public TerminalPsCommand(
        AppHostConnectionResolver connectionResolver,
        ILogger<TerminalPsCommand> logger,
        CommonCommandServices services)
        : base("ps", "List interactive terminal sessions in the connected AppHost.", services)
    {
        _interactionService = services.InteractionService;
        _logger = logger;
        _connectionResolver = connectionResolver;

        Options.Add(s_appHostOption);
        Options.Add(s_formatOption);
        Options.Add(s_verboseOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var format = parseResult.GetValue(s_formatOption);
        var verbose = parseResult.GetValue(s_verboseOption);

        _logger.LogDebug(
            "Starting 'terminal ps' (format={Format}, verbose={Verbose}, appHost={AppHost})",
            format,
            verbose,
            passedAppHostProjectFile?.FullName ?? "<auto-detected>");

        var connectionResult = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, "list terminals"),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!connectionResult.Success)
        {
            // Project-resolution failures (bad --apphost path, no AppHost project found, missing SDK)
            // must surface as an error with the correct non-zero exit code so scripts/CI can detect them.
            // For "no running AppHost" (a normal state for `ps`), the JSON branch still emits `[]` so
            // structured consumers don't have to special-case it.
            if (connectionResult.IsProjectResolutionError)
            {
                return CommandResult.FromExitCode(AppHostConnectionResultHandler.DisplayFailureAsInformation(connectionResult, _interactionService));
            }
            if (format == OutputFormat.Json)
            {
                _interactionService.DisplayRawText("[]", ConsoleOutput.Standard);
                return CommandResult.Success();
            }
            _interactionService.DisplayMessage(KnownEmojis.Information, connectionResult.ErrorMessage);
            return CommandResult.Success();
        }

        var connection = connectionResult.Connection!;

        if (!connection.SupportsTerminalsV1)
        {
            // Don't downgrade to 'no terminals' — older AppHosts may still have terminal-enabled
            // resources visible via 'terminal attach', so a misleading empty list would be worse
            // than an explicit incompatibility error.
            _interactionService.DisplayError(
                "The connected AppHost does not support 'aspire terminal ps'. Update Aspire.Hosting to a build that advertises the 'terminals.v1' capability.");
            return CommandResult.Failure(CliExitCodes.AppHostIncompatible);
        }

        var response = await _interactionService.ShowStatusAsync(
            "Listing terminal sessions...",
            async () => await connection.ListTerminalsAsync(cancellationToken).ConfigureAwait(false));

        if (response.Terminals.Length == 0 && (response.AppHostTerminals is null || response.AppHostTerminals.Length == 0))
        {
            if (format == OutputFormat.Json)
            {
                _interactionService.DisplayRawText("[]", ConsoleOutput.Standard);
            }
            else
            {
                _interactionService.DisplayMessage(KnownEmojis.Information,
                    "No terminals are running in the connected AppHost. Resources opt in with `.WithTerminal()`; AppHost-owned terminals appear here while they are open.");
            }
            return CommandResult.Success();
        }

        _logger.LogDebug(
            "ListTerminalsAsync returned {ResourceCount} resource terminal(s) (reachable={Reachable}) and {AppHostCount} AppHost terminal(s)",
            response.Terminals.Length,
            response.Terminals.Count(t => t.IsHostReachable),
            response.AppHostTerminals?.Length ?? 0);

        if (format == OutputFormat.Json)
        {
            EmitJson(response, verbose);
        }
        else
        {
            DisplayTable(response, verbose);
        }

        return CommandResult.Success();
    }

    private void EmitJson(Aspire.Cli.Backchannel.ListTerminalsResponse response, bool verbose)
    {
        var dtos = new List<TerminalPsJsonEntry>(response.Terminals.Length);
        foreach (var terminal in response.Terminals)
        {
            var replicas = new List<TerminalPsJsonReplica>(terminal.Replicas?.Length ?? 0);
            if (terminal.Replicas is not null)
            {
                foreach (var replica in terminal.Replicas)
                {
                    replicas.Add(new TerminalPsJsonReplica
                    {
                        ReplicaIndex = replica.ReplicaIndex,
                        IsAlive = replica.IsAlive,
                        ExitCode = replica.ExitCode,
                        ProducerConnected = replica.ProducerConnected,
                        RestartCount = replica.RestartCount,
                        CurrentColumns = replica.CurrentColumns,
                        CurrentRows = replica.CurrentRows,
                        AttachedPeerCount = replica.AttachedPeerCount,
                        Peers = verbose && replica.Peers is not null
                            ? replica.Peers.Select(p => new TerminalPsJsonPeer
                            {
                                PeerId = p.PeerId,
                                DisplayName = p.DisplayName,
                            }).ToArray()
                            : null,
                    });
                }
            }

            dtos.Add(new TerminalPsJsonEntry
            {
                Owner = "resource",
                ResourceName = terminal.ResourceName,
                DisplayName = terminal.DisplayName,
                ConfiguredColumns = terminal.ConfiguredColumns,
                ConfiguredRows = terminal.ConfiguredRows,
                IsHostReachable = terminal.IsHostReachable,
                Replicas = replicas.ToArray(),
            });
        }

        foreach (var terminal in response.AppHostTerminals ?? [])
        {
            dtos.Add(new TerminalPsJsonEntry
            {
                Owner = "apphost",
                TerminalId = terminal.TerminalId,
                DisplayName = terminal.Title,
                Placement = terminal.Placement,
            });
        }

        var json = JsonSerializer.Serialize(dtos, TerminalPsJsonContext.Default.ListTerminalPsJsonEntry);
        _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
    }

    private void DisplayTable(Aspire.Cli.Backchannel.ListTerminalsResponse response, bool verbose)
    {
        if (response.Terminals.Length > 0)
        {
            DisplayResourceTerminals(response);
        }

        if (response.AppHostTerminals is { Length: > 0 } appHostTerminals)
        {
            DisplayAppHostTerminals(appHostTerminals);
        }

        if (verbose)
        {
            DisplayPeerDetails(response);
        }
    }

    /// <summary>
    /// Renders terminals whose workload runs in the AppHost process.
    /// </summary>
    /// <remarks>
    /// Kept in its own table rather than merged with the resource table because none of the resource
    /// columns — replica, liveness, size, peers, restarts — mean anything for these. Merging would produce
    /// a row of placeholders that implies the data is missing rather than inapplicable.
    /// </remarks>
    private void DisplayAppHostTerminals(Aspire.Cli.Backchannel.AppHostTerminalSummary[] terminals)
    {
        var table = new Table
        {
            Title = new TableTitle("AppHost terminals"),
        };

        table.AddBoldColumn("Terminal");
        table.AddBoldColumn("Id");
        table.AddBoldColumn("Shown");

        foreach (var terminal in terminals)
        {
            table.AddRow(
                Markup.Escape(terminal.Title),
                Markup.Escape(terminal.TerminalId),
                Markup.Escape(DescribePlacement(terminal.Placement)));
        }

        _interactionService.DisplayRenderable(table);
    }

    /// <summary>
    /// Turns a <c>TerminalPlacement</c> value into something readable, passing through anything the AppHost
    /// added after this CLI was built rather than hiding it behind "unknown".
    /// </summary>
    private static string DescribePlacement(string placement) => placement switch
    {
        "Dock" => "dock",
        "Dialog" => "dialog",
        "ResourceView" => "resource view",
        "None" => "not shown",
        _ => placement,
    };

    private void DisplayResourceTerminals(Aspire.Cli.Backchannel.ListTerminalsResponse response)
    {
        var table = new Table
        {
            Title = new TableTitle("Resource terminals"),
        };

        table.AddBoldColumn("Resource");
        table.AddBoldColumn("Replica");
        table.AddBoldColumn("Status");
        table.AddBoldColumn("Size");
        table.AddBoldColumn("Peers");
        table.AddBoldColumn("Restarts");

        foreach (var terminal in response.Terminals)
        {
            // Host-unreachable terminals get one row with a placeholder so users understand why
            // detail is missing rather than silently omitting them.
            if (!terminal.IsHostReachable || terminal.Replicas is null || terminal.Replicas.Length == 0)
            {
                table.AddRow(
                    Markup.Escape(terminal.DisplayName),
                    "-",
                    "[yellow]host unreachable[/]",
                    string.Format(CultureInfo.InvariantCulture, "{0}x{1}", terminal.ConfiguredColumns, terminal.ConfiguredRows),
                    "-",
                    "-");
                continue;
            }

            foreach (var replica in terminal.Replicas)
            {
                var status = replica.IsAlive
                    ? "[green]alive[/]"
                    : replica.ExitCode is { } code
                        ? string.Format(CultureInfo.InvariantCulture, "exited ({0})", code)
                        : "waiting";

                var size = replica.CurrentColumns is { } cols && replica.CurrentRows is { } rows
                    ? string.Format(CultureInfo.InvariantCulture, "{0}x{1}", cols, rows)
                    : string.Format(CultureInfo.InvariantCulture, "{0}x{1}", terminal.ConfiguredColumns, terminal.ConfiguredRows);

                var peerCount = replica.AttachedPeerCount?.ToString(CultureInfo.InvariantCulture) ?? "-";

                table.AddRow(
                    Markup.Escape(terminal.DisplayName),
                    replica.ReplicaIndex.ToString(CultureInfo.InvariantCulture),
                    status,
                    size,
                    peerCount,
                    replica.RestartCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        _interactionService.DisplayRenderable(table);
    }

    private void DisplayPeerDetails(Aspire.Cli.Backchannel.ListTerminalsResponse response)
    {
        var hasAnyPeer = response.Terminals
            .Any(t => t.Replicas is not null && t.Replicas.Any(r => r.Peers is { Length: > 0 }));
        if (!hasAnyPeer)
        {
            return;
        }

        var peers = new Table();
        peers.AddBoldColumn("Resource");
        peers.AddBoldColumn("Replica");
        peers.AddBoldColumn("Peer Id");
        peers.AddBoldColumn("Display Name");

        foreach (var terminal in response.Terminals)
        {
            if (terminal.Replicas is null)
            {
                continue;
            }
            foreach (var replica in terminal.Replicas)
            {
                if (replica.Peers is null)
                {
                    continue;
                }
                foreach (var peer in replica.Peers)
                {
                    peers.AddRow(
                        Markup.Escape(terminal.DisplayName),
                        replica.ReplicaIndex.ToString(CultureInfo.InvariantCulture),
                        Markup.Escape(peer.PeerId),
                        Markup.Escape(peer.DisplayName ?? "-"));
                }
            }
        }

        _interactionService.DisplayRenderable(peers);
    }
}

internal sealed class TerminalPsJsonEntry
{
    /// <summary>
    /// Gets what owns the terminal's workload: <c>resource</c> or <c>apphost</c>. Discriminates which of the
    /// remaining properties are populated.
    /// </summary>
    public required string Owner { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TerminalId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceName { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets where an AppHost terminal is displayed. Absent for resource terminals, which are always shown
    /// on their resource.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Placement { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConfiguredColumns { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConfiguredRows { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsHostReachable { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerminalPsJsonReplica[]? Replicas { get; init; }
}

internal sealed class TerminalPsJsonReplica
{
    public required int ReplicaIndex { get; init; }
    public required bool IsAlive { get; init; }
    public int? ExitCode { get; init; }
    public required bool ProducerConnected { get; init; }
    public required int RestartCount { get; init; }
    public int? CurrentColumns { get; init; }
    public int? CurrentRows { get; init; }
    public int? AttachedPeerCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerminalPsJsonPeer[]? Peers { get; init; }
}

internal sealed class TerminalPsJsonPeer
{
    public required string PeerId { get; init; }
    public string? DisplayName { get; init; }
}

[JsonSerializable(typeof(List<TerminalPsJsonEntry>))]
[JsonSerializable(typeof(TerminalPsJsonEntry))]
[JsonSerializable(typeof(TerminalPsJsonReplica))]
[JsonSerializable(typeof(TerminalPsJsonPeer))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TerminalPsJsonContext : JsonSerializerContext
{
}
