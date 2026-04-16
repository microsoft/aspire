// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class WaitCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ResourceManagement;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<WaitCommand> _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly Argument<string> s_resourceArgument = new("resource")
    {
        Description = WaitCommandStrings.ResourceArgumentDescription
    };

    private static readonly Option<string> s_statusOption = new("--status")
    {
        Description = WaitCommandStrings.StatusOptionDescription,
        DefaultValueFactory = _ => "healthy"
    };

    private static readonly Option<int> s_timeoutOption = new("--timeout")
    {
        Description = WaitCommandStrings.TimeoutOptionDescription,
        DefaultValueFactory = _ => 120
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    public WaitCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        ILogger<WaitCommand> logger,
        AspireCliTelemetry telemetry,
        TimeProvider? timeProvider = null)
        : base("wait", WaitCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        Arguments.Add(s_resourceArgument);
        Options.Add(s_statusOption);
        Options.Add(s_timeoutOption);
        Options.Add(s_appHostOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var status = parseResult.GetValue(s_statusOption)!.ToLowerInvariant();
        var timeoutSeconds = parseResult.GetValue(s_timeoutOption);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);

        // Validate status value
        if (!IsValidStatus(status))
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.InvalidStatusValue, status));
            return ExitCodeConstants.InvalidCommand;
        }

        // Validate timeout
        if (timeoutSeconds <= 0)
        {
            _interactionService.DisplayError(WaitCommandStrings.TimeoutMustBePositive);
            return ExitCodeConstants.InvalidCommand;
        }

        // Resolve connection to a running AppHost
        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, WaitCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            _interactionService.DisplayError(result.ErrorMessage);
            return ExitCodeConstants.FailedToFindProject;
        }

        var connection = result.Connection!;

        var resourceResolution = await ResolveResourceNameAsync(connection, resourceName, cancellationToken).ConfigureAwait(false);
        if (resourceResolution.IsAmbiguous)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.AmbiguousResourceName, resourceName, resourceResolution.AmbiguousMatches));
            return ExitCodeConstants.WaitResourceFailed;
        }

        var resolvedResourceName = resourceResolution.ResolvedResourceName ?? resourceName;

        return await WaitForResourceAsync(connection, resourceName, resolvedResourceName, status, timeoutSeconds, cancellationToken);
    }

    private async Task<int> WaitForResourceAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        string resolvedResourceName,
        string status,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var statusLabel = GetStatusLabel(status);

        if (string.Equals(resourceName, resolvedResourceName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Waiting for resource '{ResourceName}' to reach status '{Status}' with timeout {Timeout}s", resourceName, status, timeoutSeconds);
        }
        else
        {
            _logger.LogDebug("Waiting for resource '{ResourceName}' resolved to '{ResolvedResourceName}' to reach status '{Status}' with timeout {Timeout}s", resourceName, resolvedResourceName, status, timeoutSeconds);
        }

        var startTimestamp = _timeProvider.GetTimestamp();

        var exitCode = await _interactionService.ShowStatusAsync(
            string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResource, resourceName, statusLabel),
            async () =>
            {
                var response = await connection.WaitForResourceAsync(resolvedResourceName, status, timeoutSeconds, cancellationToken).ConfigureAwait(false);

                if (response.Success)
                {
                    return ExitCodeConstants.Success;
                }

                if (response.ResourceNotFound)
                {
                    _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceNotFound, resourceName));
                    return ExitCodeConstants.WaitResourceFailed;
                }

                if (response.TimedOut)
                {
                    _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitTimedOut, resourceName, statusLabel, timeoutSeconds));
                    return ExitCodeConstants.WaitTimeout;
                }

                // Resource entered a failed state
                _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceEnteredFailedState, resourceName, response.State ?? response.ErrorMessage));
                return ExitCodeConstants.WaitResourceFailed;
            });

        // Reset cursor position after spinner
        _interactionService.DisplayPlainText("");

        if (exitCode == ExitCodeConstants.Success)
        {
            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
            _interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceReachedTargetStatus, resourceName, statusLabel, elapsed.TotalSeconds));
        }

        return exitCode;
    }

    private static async Task<ResourceResolutionResult> ResolveResourceNameAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var snapshots = await connection.GetResourceSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var resolvedResources = ResourceSnapshotMapper.ResolveResources(resourceName, snapshots);
        if (resolvedResources.Count == 1)
        {
            return new ResourceResolutionResult(resolvedResources[0].Name);
        }

        var ambiguousMatches = snapshots
            .Where(s => string.Equals(s.DisplayName, resourceName, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToList();

        return ambiguousMatches.Count > 1
            ? new ResourceResolutionResult(null, string.Join(", ", ambiguousMatches))
            : new ResourceResolutionResult(null);
    }

    private static bool IsValidStatus(string status)
    {
        return status is "healthy" or "up" or "down";
    }

    private static string GetStatusLabel(string status)
    {
        return status switch
        {
            "up" => "up (running)",
            "healthy" => "healthy",
            "down" => "down",
            _ => status
        };
    }

    private sealed record ResourceResolutionResult(string? ResolvedResourceName, string? AmbiguousMatches = null)
    {
        public bool IsAmbiguous => AmbiguousMatches is not null;
    }
}
