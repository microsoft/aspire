// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Processes;
using Aspire.Cli.Resources;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed partial class StopCommand
{
    private readonly IAuxiliaryBackchannelMonitor _backchannelMonitor;
    private readonly IProcessIdentityProvider _processIdentityProvider;
    private readonly TrayProtocolOutput _protocolOutput;
    private static readonly Option<int?> s_protocolVersionOption = new("--protocol-version") { Hidden = true };
    private static readonly Option<long?> s_startedAtOption = new("--started-at") { Hidden = true };
    private static readonly Option<OutputFormat> s_formatOption = new("--format") { Hidden = true };

    protected override bool IsJsonFormatRequested(ParseResult parseResult)
        => parseResult.GetValue(s_protocolVersionOption) is not null || base.IsJsonFormatRequested(parseResult);

    private async Task<CommandResult> ExecuteProtocolAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        TrayStopMessage response;
        try
        {
            response = await StopProtocolInstanceAsync(parseResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response = StopResponse("stop_failed", CliExitCodes.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The tray protocol stop request failed.");
            response = StopResponse("stop_failed", CliExitCodes.FailedToDotnetRunAppHost);
        }

        var json = JsonSerializer.Serialize(response, TrayCliJsonContext.Default.TrayStopMessage);
        // Cancellation is itself a typed stop failure; do not suppress its final response.
        await _protocolOutput.WriteLineAsync(json, CancellationToken.None).ConfigureAwait(false);
        return CommandResult.FromExitCode(response.ExitCode);
    }

    private async Task<TrayStopMessage> StopProtocolInstanceAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var appHostFile = parseResult.GetValue(s_appHostOption.InnerOption);
        var appHostArgument = parseResult.GetResult(s_appHostOption.InnerOption)?.Tokens.SingleOrDefault()?.Value;
        var processId = parseResult.GetValue(s_pidOption);
        var startedAt = parseResult.GetValue(s_startedAtOption);
        if (parseResult.GetValue(s_protocolVersionOption) != TrayCliProtocol.Version ||
            parseResult.GetValue(s_formatOption) != OutputFormat.Json ||
            appHostFile is null || appHostArgument is null || !Path.IsPathFullyQualified(appHostArgument) ||
            processId is not > 0 || startedAt is not > 0 ||
            parseResult.GetResult(s_allOption) is { Implicit: false } ||
            parseResult.GetResult(s_forceOption) is { Implicit: false } ||
            parseResult.GetResult(s_volumesOption) is { Implicit: false })
        {
            InteractionService.DisplayError(StopCommandStrings.ProtocolRequiresExactIdentity);
            return StopResponse("invalid_request", CliExitCodes.InvalidCommand);
        }

        await _backchannelMonitor.ScanAsync(cancellationToken, pruneOrphanedSockets: false, throwOnDiscoveryFailure: true).ConfigureAwait(false);
        var matching = _backchannelMonitor.Connections
            .Where(connection => connection.AppHostInfo is { } info &&
                info.ProcessId == processId && GetAppHostPathComparer().Equals(info.AppHostPath, appHostFile.FullName))
            .ToArray();
        if (matching.Length == 0)
        {
            return StopResponse("not_found", CliExitCodes.FailedToFindProject);
        }
        if (matching.Length != 1)
        {
            return StopResponse("ambiguous", CliExitCodes.FailedToFindProject);
        }

        var connection = matching[0];
        var actualStartedAt = _processIdentityProvider.GetStartTimeUnixMilliseconds(processId.Value);
        if (actualStartedAt is not > 0)
        {
            return StopResponse("identity_unavailable", CliExitCodes.FailedToFindProject);
        }
        if (actualStartedAt != startedAt)
        {
            return StopResponse("identity_mismatch", CliExitCodes.FailedToFindProject);
        }

        // Both the lifetime check and subsequent exit observation refer to this selection.
        // Never reconnect or reuse project-wide resolution if the original RPC connection dies.
        var identity = new AppHostInformation
        {
            AppHostPath = appHostFile.FullName,
            ProcessId = processId.Value,
            StableStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(actualStartedAt.Value)
        };
        var stopped = await _processShutdownService.StopAppHostByConnectionAsync(identity, connection.StopAppHostAsync, cancellationToken).ConfigureAwait(false);
        if (!stopped)
        {
            return StopResponse("stop_failed", CliExitCodes.FailedToDotnetRunAppHost);
        }

        connection.Socket.TryDelete();
        return StopResponse("stopped", CliExitCodes.Success);
    }

    private static TrayStopMessage StopResponse(string outcome, int exitCode) => new()
    {
        Version = TrayCliProtocol.Version,
        Outcome = outcome,
        ExitCode = exitCode
    };
}
