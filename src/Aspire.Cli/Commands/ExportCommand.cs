// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared.Export;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to export telemetry and resource data to a zip file.
/// </summary>
internal sealed class ExportCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<ExportCommand> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    private static readonly Option<string?> s_outputOption = new("--output", "-o")
    {
        Description = ExportCommandStrings.OutputOptionDescription
    };

    private static readonly Argument<string?> s_resourceArgument = new("resource")
    {
        Description = ExportCommandStrings.ResourceOptionDescription,
        Arity = ArgumentArity.ZeroOrOne
    };

    public ExportCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<ExportCommand> logger)
        : base("export", ExportCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_outputOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var outputPath = parseResult.GetValue(s_outputOption);

        // Resolve the AppHost connection for backchannel access
        var connectionResult = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, ExportCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!connectionResult.Success)
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, connectionResult.ErrorMessage);
            return ExitCodeConstants.Success;
        }

        var connection = connectionResult.Connection!;

        // Get dashboard API info for telemetry data
        var dashboardInfo = await connection.GetDashboardInfoV2Async(cancellationToken);
        var isDashboardAvailable = dashboardInfo?.ApiBaseUrl is not null && dashboardInfo.ApiToken is not null;

        if (!isDashboardAvailable)
        {
            _interactionService.DisplayMessage(KnownEmojis.Warning, ExportCommandStrings.DashboardNotAvailable);
        }

        var baseUrl = dashboardInfo?.ApiBaseUrl;
        var apiToken = dashboardInfo?.ApiToken;

        // Default file name if not specified
        if (string.IsNullOrEmpty(outputPath))
        {
            var timestamp = _timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            outputPath = $"aspire-export-{timestamp}.zip";
        }

        // Ensure directory exists
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            using var client = isDashboardAvailable ? TelemetryCommandHelpers.CreateApiClient(_httpClientFactory, apiToken!) : null;

            // Get telemetry resources and resource snapshots
            var (telemetryResources, snapshots) = await _interactionService.ShowStatusAsync(ExportCommandStrings.GatheringResources, async () =>
            {
                var resources = isDashboardAvailable
                    ? await TelemetryCommandHelpers.GetAllResourcesAsync(client!, baseUrl!, cancellationToken).ConfigureAwait(false)
                    : [];
                var snaps = await connection.GetResourceSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                return (resources, snaps);
            });

            // Validate resource name exists (match by Name or DisplayName since users may pass either)
            if (resourceName is not null)
            {
                if (!ResourceSnapshotMapper.WhereMatchesResourceName(snapshots, resourceName).Any())
                {
                    _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ExportCommandStrings.ResourceNotFound, resourceName));
                    return ExitCodeConstants.InvalidCommand;
                }
            }
            else
            {
                if (snapshots.Count == 0)
                {
                    _interactionService.DisplayMessage(KnownEmojis.Information, ExportCommandStrings.NoResourcesFound);
                    return ExitCodeConstants.Success;
                }
            }

            // Resolve which telemetry resources match the filter
            List<string>? resolvedTelemetryResources = null;
            var hasTelemetryData = true;
            if (resourceName is not null)
            {
                hasTelemetryData = TelemetryCommandHelpers.TryResolveResourceNames(resourceName, telemetryResources, out resolvedTelemetryResources);
            }

            var allOtlpResources = TelemetryCommandHelpers.ToOtlpResources(telemetryResources);

            var exportArchive = new ExportArchive();

            // 1. Export resource details (filtered when a resource name is specified)
            AddResources(exportArchive, snapshots, resourceName);

            // 2. Export console logs from backchannel
            await _interactionService.ShowStatusAsync(ExportCommandStrings.GatheringConsoleLogs, async () =>
            {
                await AddConsoleLogsAsync(exportArchive, connection, resourceName, snapshots, cancellationToken).ConfigureAwait(false);
                return true;
            });

            if (isDashboardAvailable && hasTelemetryData)
            {
                // 3. Export structured logs from Dashboard API (skip if resource has no telemetry data or dashboard is unavailable)
                await _interactionService.ShowStatusAsync(ExportCommandStrings.GatheringStructuredLogs, async () =>
                {
                    await AddStructuredLogsAsync(exportArchive, client!, baseUrl!, resolvedTelemetryResources, allOtlpResources, cancellationToken).ConfigureAwait(false);
                    return true;
                });

                // 4. Export traces from Dashboard API (skip if resource has no telemetry data or dashboard is unavailable)
                await _interactionService.ShowStatusAsync(ExportCommandStrings.GatheringTraces, async () =>
                {
                    await AddTracesAsync(exportArchive, client!, baseUrl!, resolvedTelemetryResources, allOtlpResources, cancellationToken).ConfigureAwait(false);
                    return true;
                });
            }

            var fullPath = Path.GetFullPath(outputPath);
            exportArchive.WriteToFile(fullPath);

            _interactionService.DisplayMessage(KnownEmojis.CheckMark, string.Format(CultureInfo.CurrentCulture, ExportCommandStrings.ExportComplete, fullPath));
            return ExitCodeConstants.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export telemetry data");
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, ExportCommandStrings.FailedToExport, ex.Message));
            return ExitCodeConstants.DashboardFailure;
        }
    }

    private static void AddResources(ExportArchive exportArchive, IReadOnlyList<ResourceSnapshot> snapshots, string? resourceName)
    {
        var resourceJsonList = ResourceSnapshotMapper.MapToResourceJsonList(snapshots);
        var matchingNames = resourceName is not null
            ? new HashSet<string>(ResourceSnapshotMapper.WhereMatchesResourceName(snapshots, resourceName).Select(s => s.Name), StringComparers.ResourceName)
            : null;

        foreach (var (snapshot, resourceJson) in snapshots.Zip(resourceJsonList))
        {
            if (matchingNames is not null && !matchingNames.Contains(snapshot.Name))
            {
                continue;
            }

            var displayName = ResourceSnapshotMapper.GetResourceName(snapshot, snapshots);
            exportArchive.Resources[displayName] = resourceJson;
        }
    }

    private static async Task AddConsoleLogsAsync(
        ExportArchive exportArchive,
        IAppHostAuxiliaryBackchannel connection,
        string? resourceName,
        IReadOnlyList<ResourceSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var logLinesByResource = new Dictionary<string, List<string>>();

        await foreach (var logLine in connection.GetResourceLogsAsync(resourceName, follow: false, cancellationToken).ConfigureAwait(false))
        {
            if (!logLinesByResource.TryGetValue(logLine.ResourceName, out var lines))
            {
                lines = [];
                logLinesByResource[logLine.ResourceName] = lines;
            }

            lines.Add(logLine.Content);
        }

        foreach (var (name, lines) in logLinesByResource)
        {
            var snapshot = snapshots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparisons.ResourceName));
            var displayName = snapshot is not null
                ? ResourceSnapshotMapper.GetResourceName(snapshot, snapshots)
                : name;
            exportArchive.ConsoleLogs[displayName] = lines;
        }
    }

    private static async Task AddStructuredLogsAsync(
        ExportArchive exportArchive,
        HttpClient client,
        string baseUrl,
        List<string>? resolvedResources,
        IReadOnlyList<IOtlpResource> allOtlpResources,
        CancellationToken cancellationToken)
    {
        var url = DashboardUrls.TelemetryLogsApiUrl(baseUrl, resolvedResources);
        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var apiResponse = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        if (apiResponse?.Data?.ResourceLogs is { Length: > 0 })
        {
            // Group by resolved resource name so each resource gets its own file
            var groups = apiResponse.Data.ResourceLogs
                .GroupBy(rl => TelemetryCommandHelpers.ResolveResourceName(rl.Resource, allOtlpResources));

            foreach (var group in groups)
            {
                exportArchive.StructuredLogs[group.Key] = new OtlpTelemetryDataJson
                {
                    ResourceLogs = group.ToArray()
                };
            }
        }
    }

    private static async Task AddTracesAsync(
        ExportArchive exportArchive,
        HttpClient client,
        string baseUrl,
        List<string>? resolvedResources,
        IReadOnlyList<IOtlpResource> allOtlpResources,
        CancellationToken cancellationToken)
    {
        var url = DashboardUrls.TelemetryTracesApiUrl(baseUrl, resolvedResources);
        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var apiResponse = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        if (apiResponse?.Data?.ResourceSpans is { Length: > 0 })
        {
            // Group by resolved resource name so each resource gets its own file
            var groups = apiResponse.Data.ResourceSpans
                .GroupBy(rs => TelemetryCommandHelpers.ResolveResourceName(rs.Resource, allOtlpResources));

            foreach (var group in groups)
            {
                exportArchive.Traces[group.Key] = new OtlpTelemetryDataJson
                {
                    ResourceSpans = group.ToArray()
                };
            }
        }
    }
}
