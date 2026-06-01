// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Represents information about a running AppHost for JSON serialization.
/// Aligned with AppHostListInfo from ListAppHostsTool.
/// </summary>
// `aspire ps --format json` uses this shape; keep docs/specs/cli-output-formats.md in sync when changing it.
internal sealed class AppHostDisplayInfo
{
    public required string AppHostPath { get; init; }
    public required int AppHostPid { get; init; }
    public string Status { get; init; } = AppHostDisplayStatus.Running;
    public string? SdkVersion { get; init; }
    public int? CliPid { get; init; }
    public string? DashboardUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogFilePath { get; init; }
}

internal static class AppHostDisplayStatus
{
    public const string Running = "running";
    public const string Stopped = "stopped";
}

[JsonSerializable(typeof(List<AppHostDisplayInfo>))]
[JsonSerializable(typeof(AppHostDisplayInfo))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class PsCommandJsonContext : JsonSerializerContext
{
    private static PsCommandJsonContext? s_relaxedEscaping;
    private static PsCommandJsonContext? s_compactRelaxedEscaping;

    /// <summary>
    /// Gets a context with relaxed JSON escaping for non-ASCII character support.
    /// </summary>
    public static PsCommandJsonContext RelaxedEscaping => s_relaxedEscaping ??= new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    /// <summary>
    /// Gets a compact context with relaxed JSON escaping for newline-delimited streaming output.
    /// </summary>
    public static PsCommandJsonContext CompactRelaxedEscaping => s_compactRelaxedEscaping ??= new(new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

internal sealed partial class PsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;
    private readonly IInteractionService _interactionService;
    private readonly IAuxiliaryBackchannelMonitor _backchannelMonitor;
    private readonly ILogger<PsCommand> _logger;
    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = PsCommandStrings.JsonOptionDescription
    };

    private static readonly Option<bool> s_followOption = new("--follow", "-f")
    {
        Description = PsCommandStrings.FollowOptionDescription
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
        Options.Add(s_followOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var format = parseResult.GetValue(s_formatOption);

        if (parseResult.GetValue(s_followOption))
        {
            return await ExecuteFollowAsync(format, cancellationToken).ConfigureAwait(false);
        }

        // Scan for running AppHosts (same as ListAppHostsTool). JSON output must not go
        // through status rendering because non-interactive status text shares stdout.
        var connections = format == OutputFormat.Json
            ? await ScanForConnectionsAsync(cancellationToken).ConfigureAwait(false)
            : await _interactionService.ShowStatusAsync(
                SharedCommandStrings.ScanningForRunningAppHosts,
                async () => await ScanForConnectionsAsync(cancellationToken).ConfigureAwait(false));

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
            return CommandResult.Success();
        }

        // Order: in-scope first, then out-of-scope
        var orderedConnections = connections
            .OrderByDescending(c => c.IsInScope)
            .ToList();

        // Gather info for each AppHost
        var appHostInfos = await GatherAppHostInfosAsync(orderedConnections, cancellationToken).ConfigureAwait(false);

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

        return CommandResult.Success();
    }

    private async Task<List<IAppHostAuxiliaryBackchannel>> ScanForConnectionsAsync(CancellationToken cancellationToken)
    {
        await _backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);

        return _backchannelMonitor.Connections.ToList();
    }

    private abstract record PsFollowUpdate;

    private sealed record ConnectionsUpdate(IReadOnlyList<IAppHostAuxiliaryBackchannel> Connections) : PsFollowUpdate;

    private async Task<CommandResult> ExecuteFollowAsync(OutputFormat format, CancellationToken cancellationToken)
    {
        if (format != OutputFormat.Json)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, PsCommandStrings.FollowRequiresJson);
        }
        using var followCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var followCancellationToken = followCancellationTokenSource.Token;
        var updates = Channel.CreateUnbounded<PsFollowUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        var appHostKeyComparer = GetAppHostKeyComparer();
        var activeAppHosts = new Dictionary<string, AppHostDisplayInfo>(appHostKeyComparer);
        var lastJsonByAppHost = new Dictionary<string, string>(appHostKeyComparer);

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var connections in _backchannelMonitor.WatchConnectionsAsync(followCancellationToken).WithCancellation(followCancellationToken).ConfigureAwait(false))
                {
                    await updates.Writer.WriteAsync(new ConnectionsUpdate(connections), followCancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (followCancellationToken.IsCancellationRequested)
            {
                // Expected when the caller stops following.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed while watching AppHost connections for ps --follow.");
            }
            finally
            {
                updates.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var update in updates.Reader.ReadAllAsync(followCancellationToken).ConfigureAwait(false))
            {
                if (update is ConnectionsUpdate connectionsUpdate)
                {
                    var currentConnections = OrderConnections(connectionsUpdate.Connections);
                    var currentAppHosts = await GatherAppHostInfosAsync(currentConnections, followCancellationToken).ConfigureAwait(false);
                    var nextActiveAppHosts = new Dictionary<string, AppHostDisplayInfo>(appHostKeyComparer);

                    foreach (var appHost in currentAppHosts)
                    {
                        nextActiveAppHosts[GetAppHostKey(appHost)] = appHost;
                        if (!await TryWriteAppHostInfoAsync(appHost).ConfigureAwait(false))
                        {
                            return CommandResult.Success();
                        }
                    }

                    foreach (var (key, appHost) in activeAppHosts)
                    {
                        if (!nextActiveAppHosts.ContainsKey(key) &&
                            !await TryWriteAppHostInfoAsync(CopyWithStatus(appHost, AppHostDisplayStatus.Stopped)).ConfigureAwait(false))
                        {
                            return CommandResult.Success();
                        }
                    }

                    activeAppHosts = nextActiveAppHosts;
                }
            }
        }
        catch (OperationCanceledException) when (followCancellationToken.IsCancellationRequested)
        {
            return CommandResult.Success();
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Stopping ps --follow because the output stream is no longer writable.");
            return CommandResult.Success();
        }
        finally
        {
            await followCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }

        return CommandResult.Success();

        async Task<bool> TryWriteAppHostInfoAsync(AppHostDisplayInfo appHost)
        {
            var key = GetAppHostKey(appHost);
            var json = JsonSerializer.Serialize(appHost, PsCommandJsonContext.CompactRelaxedEscaping.AppHostDisplayInfo);
            if (lastJsonByAppHost.TryGetValue(key, out var lastJson) &&
                string.Equals(json, lastJson, StringComparison.Ordinal))
            {
                return true;
            }

            lastJsonByAppHost[key] = json;

            try
            {
                _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "Stopping ps --follow because the output stream is no longer writable.");
                await followCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                return false;
            }
        }
    }
}

internal sealed partial class PsCommand
{
    private static string GetAppHostKey(AppHostDisplayInfo appHost)
    {
        return string.Concat(appHost.AppHostPath, "\0", appHost.AppHostPid.ToString(CultureInfo.InvariantCulture));
    }

    private static StringComparer GetAppHostKeyComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static AppHostDisplayInfo CopyWithStatus(AppHostDisplayInfo appHost, string status)
    {
        return new AppHostDisplayInfo
        {
            AppHostPath = appHost.AppHostPath,
            AppHostPid = appHost.AppHostPid,
            Status = status,
            SdkVersion = appHost.SdkVersion,
            CliPid = appHost.CliPid,
            DashboardUrl = appHost.DashboardUrl,
            LogFilePath = appHost.LogFilePath
        };
    }

    private static List<IAppHostAuxiliaryBackchannel> OrderConnections(IEnumerable<IAppHostAuxiliaryBackchannel> connections)
    {
        return connections
            .OrderByDescending(c => c.IsInScope)
            .ToList();
    }

    private async Task<List<AppHostDisplayInfo>> GatherAppHostInfosAsync(List<IAppHostAuxiliaryBackchannel> connections, CancellationToken cancellationToken)
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
            var cliLogFilePath = info.CliLogFilePath;

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
                        cliLogFilePath = v2Info.CliLogFilePath ?? cliLogFilePath;

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

            appHostInfos.Add(new AppHostDisplayInfo
            {
                AppHostPath = appHostPath ?? PsCommandStrings.UnknownPath,
                AppHostPid = appHostPid,
                Status = AppHostDisplayStatus.Running,
                SdkVersion = sdkVersion,
                CliPid = cliPid,
                DashboardUrl = dashboardUrl,
                LogFilePath = cliLogFilePath
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
        table.AddBoldColumn(SharedCommandStrings.HeaderStatus);
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
                    dashboard = MarkupHelpers.SafeLink(_interactionService, appHost.DashboardUrl);
                }
                else
                {
                    dashboard = Markup.Escape(appHost.DashboardUrl);
                }
            }

            var columns = new List<string>
            {
                Markup.Escape(shortPath),
                Markup.Escape(appHost.Status),
                Markup.Escape(appHost.SdkVersion ?? "-"),
                appHost.AppHostPid.ToString(CultureInfo.InvariantCulture),
                cliPid,
            };

            columns.Add(dashboard);

            table.AddRow(columns.ToArray());
        }

        _interactionService.DisplayRenderable(table);
    }

}
