// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Shared.Model.Serialization;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Represents information about a running AppHost for JSON serialization.
/// Aligned with AppHostListInfo from ListAppHostsTool.
/// </summary>
internal sealed class AppHostDisplayInfo
{
    public required string AppHostPath { get; init; }
    public required int AppHostPid { get; init; }
    public string? SdkVersion { get; init; }
    public int? CliPid { get; init; }
    public string? DashboardUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResourceJson>? Resources { get; set; }
}

[JsonSerializable(typeof(List<AppHostDisplayInfo>))]
[JsonSerializable(typeof(ResourceJson))]
[JsonSerializable(typeof(ResourceUrlJson))]
[JsonSerializable(typeof(ResourceVolumeJson))]
[JsonSerializable(typeof(ResourceRelationshipJson))]
[JsonSerializable(typeof(ResourceHealthReportJson))]
[JsonSerializable(typeof(ResourceCommandJson))]
[JsonSerializable(typeof(ResourceCommandArgumentJson[]))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, ResourceHealthReportJson>))]
[JsonSerializable(typeof(Dictionary<string, ResourceCommandJson>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class PsCommandJsonContext : JsonSerializerContext
{
    private static PsCommandJsonContext? s_relaxedEscaping;

    /// <summary>
    /// Gets a context with relaxed JSON escaping for non-ASCII character support.
    /// </summary>
    public static PsCommandJsonContext RelaxedEscaping => s_relaxedEscaping ??= new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

internal sealed class PsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IInteractionService _interactionService;
    private readonly IAuxiliaryBackchannelMonitor _backchannelMonitor;
    private readonly ILogger<PsCommand> _logger;
    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = PsCommandStrings.JsonOptionDescription
    };

    private static readonly Option<bool> s_resourcesOption = new("--resources")
    {
        Description = PsCommandStrings.ResourcesOptionDescription
    };

    public PsCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<PsCommand> logger)
        : base("ps", PsCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _backchannelMonitor = backchannelMonitor;
        _logger = logger;

        Options.Add(s_formatOption);
        Options.Add(s_resourcesOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var format = parseResult.GetValue(s_formatOption);
        var includeResources = parseResult.GetValue(s_resourcesOption);

        // Scan for running AppHosts (same as ListAppHostsTool)
        // Skip status display for JSON output to avoid contaminating stdout
        var connections = await _interactionService.ShowStatusAsync(
            SharedCommandStrings.ScanningForRunningAppHosts,
            async () =>
            {
                await _backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
                return _backchannelMonitor.Connections.ToList();
            });

        if (connections.Count == 0)
        {
            if (format == OutputFormat.Json)
            {
                // Structured output always goes to stdout.
                _interactionService.DisplayRawText("[]", ConsoleOutput.Standard);
            }
            else
            {
                _interactionService.DisplayMessage(KnownEmojis.Information, SharedCommandStrings.AppHostNotRunning);
            }
            return ExitCodeConstants.Success;
        }

        // Order: in-scope first, then out-of-scope
        var orderedConnections = connections
            .OrderByDescending(c => c.IsInScope)
            .ToList();

        // Gather info for each AppHost
        var appHostInfos = await GatherAppHostInfosAsync(orderedConnections, includeResources && format == OutputFormat.Json, cancellationToken).ConfigureAwait(false);

        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(appHostInfos, PsCommandJsonContext.RelaxedEscaping.ListAppHostDisplayInfo);
            // Structured output always goes to stdout.
            _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            DisplayTable(appHostInfos);
        }

        return ExitCodeConstants.Success;
    }

    private async Task<List<AppHostDisplayInfo>> GatherAppHostInfosAsync(List<IAppHostAuxiliaryBackchannel> connections, bool includeResources, CancellationToken cancellationToken)
    {
        var appHostInfos = new List<AppHostDisplayInfo>();

        foreach (var connection in connections)
        {
            var info = connection.AppHostInfo;
            if (info is null)
            {
                continue;
            }

            string? sdkVersion = null;
            var appHostPath = info.AppHostPath;
            var appHostPid = info.ProcessId;
            var cliPid = info.CliProcessId;

            try
            {
                if (connection.SupportsV2)
                {
                    var v2Info = await connection.GetAppHostInfoV2Async(cancellationToken).ConfigureAwait(false);
                    if (v2Info is not null)
                    {
                        sdkVersion = GetSdkVersion(v2Info.AspireHostVersion);
                        appHostPath = string.IsNullOrWhiteSpace(v2Info.AppHostPath) ? appHostPath : v2Info.AppHostPath;
                        cliPid = v2Info.CliProcessId ?? cliPid;

                        if (int.TryParse(v2Info.Pid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid))
                        {
                            appHostPid = parsedPid;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get AppHost SDK version for {AppHostPath}", info.AppHostPath);
            }

            string? dashboardUrl = null;

            try
            {
                var dashboardUrls = await connection.GetDashboardUrlsAsync(cancellationToken).ConfigureAwait(false);
                dashboardUrl = dashboardUrls?.BaseUrlWithLoginToken;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get dashboard URL for {AppHostPath}", info.AppHostPath);
            }

            List<ResourceJson>? resources = null;
            if (includeResources)
            {
                try
                {
                    var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
                    resources = ResourceSnapshotMapper.MapToResourceJsonList(snapshots, dashboardUrl, includeEnvironmentVariableValues: false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get resource snapshots for {AppHostPath}", info.AppHostPath);
                }
            }

            appHostInfos.Add(new AppHostDisplayInfo
            {
                AppHostPath = appHostPath ?? PsCommandStrings.UnknownPath,
                AppHostPid = appHostPid,
                SdkVersion = sdkVersion,
                CliPid = cliPid,
                DashboardUrl = dashboardUrl,
                Resources = resources
            });
        }

        return appHostInfos;
    }

    private static string? GetSdkVersion(string? sdkVersion)
    {
        if (string.IsNullOrWhiteSpace(sdkVersion) ||
            string.Equals(sdkVersion, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return sdkVersion;
    }

    private void DisplayTable(List<AppHostDisplayInfo> appHosts)
    {
        if (appHosts.Count == 0)
        {
            return;
        }

        var shortPaths = FileSystemHelper.ShortenPaths(appHosts.Select(a => a.AppHostPath).ToList());

        var table = new Table();
        table.AddBoldColumn(PsCommandStrings.HeaderPath);
        table.AddBoldColumn(PsCommandStrings.HeaderSdk);
        table.AddBoldColumn(PsCommandStrings.HeaderPid);
        table.AddBoldColumn(PsCommandStrings.HeaderCliPid);
        table.AddBoldColumn(PsCommandStrings.HeaderDashboard);

        foreach (var appHost in appHosts)
        {
            var shortPath = shortPaths[appHost.AppHostPath];
            var cliPid = appHost.CliPid?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var dashboard = "-";
            if (!string.IsNullOrEmpty(appHost.DashboardUrl))
            {
                if (Uri.TryCreate(appHost.DashboardUrl, UriKind.Absolute, out _))
                {
                    dashboard = $"[link={Markup.Escape(appHost.DashboardUrl)}]{Markup.Escape(appHost.DashboardUrl)}[/]";
                }
                else
                {
                    dashboard = Markup.Escape(appHost.DashboardUrl);
                }
            }

            table.AddRow(
                Markup.Escape(shortPath),
                Markup.Escape(appHost.SdkVersion ?? "-"),
                appHost.AppHostPid.ToString(CultureInfo.InvariantCulture),
                cliPid,
                dashboard);
        }

        _interactionService.DisplayRenderable(table);
    }

}
