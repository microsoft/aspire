// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed record TelemetryAnalysisData(
    OtlpResourceSpansJson[] ResourceSpans,
    IReadOnlyList<IOtlpResource> AllResources,
    string? DashboardUrl);

internal sealed record TelemetryAnalysisDataResult(
    bool Success,
    TelemetryAnalysisData? Data,
    int ExitCode,
    string? ErrorMessage)
{
    public static TelemetryAnalysisDataResult Succeeded(TelemetryAnalysisData data) => new(true, data, CliExitCodes.Success, null);

    public static TelemetryAnalysisDataResult Failed(int exitCode, string? errorMessage = null) => new(false, null, exitCode, errorMessage);
}

internal sealed class TelemetryAnalysisDataSource
{
    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelemetryAnalysisDataSource> _logger;

    public TelemetryAnalysisDataSource(
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IInteractionService interactionService,
        IProjectLocator projectLocator,
        CliExecutionContext executionContext,
        IHttpClientFactory httpClientFactory,
        ILogger<TelemetryAnalysisDataSource> logger)
    {
        _interactionService = interactionService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, projectLocator, executionContext, logger);
    }

    public async Task<TelemetryAnalysisDataResult> GetTracesAsync(
        FileInfo? projectFile,
        string? dashboardUrl,
        string? apiKey,
        FileInfo? archiveFile,
        string? resourceName,
        CancellationToken cancellationToken)
    {
        if (archiveFile is not null)
        {
            return await GetArchivedTracesAsync(archiveFile, resourceName, cancellationToken).ConfigureAwait(false);
        }

        return await GetLiveTracesAsync(projectFile, dashboardUrl, apiKey, resourceName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TelemetryAnalysisDataResult> GetLiveTracesAsync(
        FileInfo? projectFile,
        string? dashboardUrl,
        string? apiKey,
        string? resourceName,
        CancellationToken cancellationToken)
    {
        var dashboardApi = await TelemetryCommandHelpers.GetDashboardApiAsync(
            _connectionResolver,
            _interactionService,
            _httpClientFactory,
            _logger,
            projectFile,
            dashboardUrl,
            apiKey,
            requireDashboard: true,
            cancellationToken).ConfigureAwait(false);

        if (!dashboardApi.Success)
        {
            return TelemetryAnalysisDataResult.Failed(dashboardApi.ExitCode);
        }

        try
        {
            using var client = TelemetryCommandHelpers.CreateApiClient(_httpClientFactory, dashboardApi.ApiToken!);
            var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, dashboardApi.BaseUrl!, cancellationToken).ConfigureAwait(false);

            if (!TelemetryCommandHelpers.TryResolveResourceNames(resourceName, resources, out var resolvedResources))
            {
                return TelemetryAnalysisDataResult.Failed(
                    CliExitCodes.InvalidCommand,
                    string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.ResourceNotFound, resourceName));
            }

            var url = DashboardUrls.TelemetryTracesApiUrl(dashboardApi.BaseUrl!, resolvedResources, limit: TelemetryCommandHelpers.MaxTelemetryLimit);
            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

            var apiResponse = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse, cancellationToken).ConfigureAwait(false);
            var resourceSpans = apiResponse?.Data?.ResourceSpans ?? [];
            var allResources = TelemetryCommandHelpers.ToOtlpResources(resources);

            return TelemetryAnalysisDataResult.Succeeded(new TelemetryAnalysisData(resourceSpans, allResources, dashboardApi.DashboardUrl));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch traces for telemetry analysis");
            var errorInfo = await TelemetryCommandHelpers.FormatTelemetryErrorAsync(ex, dashboardApi.BaseUrl!, dashboardUrl is not null, _httpClientFactory, _logger, cancellationToken).ConfigureAwait(false);
            TelemetryCommandHelpers.DisplayTelemetryError(_interactionService, errorInfo);
            return TelemetryAnalysisDataResult.Failed(CliExitCodes.DashboardFailure);
        }
    }

    private async Task<TelemetryAnalysisDataResult> GetArchivedTracesAsync(
        FileInfo archiveFile,
        string? resourceName,
        CancellationToken cancellationToken)
    {
        if (!archiveFile.Exists)
        {
            return TelemetryAnalysisDataResult.Failed(
                CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.TelemetryArchiveNotFound, archiveFile.FullName));
        }

        try
        {
            using var fileStream = archiveFile.OpenRead();
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);

            var resourceSpans = new List<OtlpResourceSpansJson>();
            foreach (var entry in archive.Entries.Where(IsTraceEntry))
            {
                await using var entryStream = entry.Open();
                var data = await JsonSerializer.DeserializeAsync(entryStream, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson, cancellationToken).ConfigureAwait(false);
                if (data?.ResourceSpans is { Length: > 0 })
                {
                    resourceSpans.AddRange(data.ResourceSpans);
                }
            }

            var allResources = GetAllResources(resourceSpans);
            var selectedResourceSpans = resourceSpans.ToArray();
            if (!string.IsNullOrEmpty(resourceName))
            {
                if (!TryFilterResourceSpans(resourceSpans, allResources, resourceName, out var filteredResourceSpans))
                {
                    return TelemetryAnalysisDataResult.Failed(
                        CliExitCodes.InvalidCommand,
                        string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.ResourceNotFound, resourceName));
                }

                selectedResourceSpans = filteredResourceSpans;
            }

            return TelemetryAnalysisDataResult.Succeeded(new TelemetryAnalysisData(
                selectedResourceSpans,
                allResources,
                DashboardUrl: null));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            _logger.LogError(ex, "Failed to read telemetry archive {ArchiveFile}", archiveFile.FullName);
            return TelemetryAnalysisDataResult.Failed(
                CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.FailedToReadTelemetryArchive, archiveFile.FullName, ex.Message));
        }
    }

    private static bool IsTraceEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.StartsWith("traces/", StringComparison.OrdinalIgnoreCase) &&
            entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            entry.Length > 0;
    }

    private static IReadOnlyList<IOtlpResource> GetAllResources(IEnumerable<OtlpResourceSpansJson> resourceSpans)
    {
        return resourceSpans
            .Select(rs => new SimpleOtlpResource(rs.Resource?.GetServiceName() ?? "unknown", rs.Resource?.GetServiceInstanceId()))
            .Distinct()
            .OrderBy(r => r.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryFilterResourceSpans(
        IReadOnlyList<OtlpResourceSpansJson> resourceSpans,
        IReadOnlyList<IOtlpResource> allResources,
        string resourceName,
        out OtlpResourceSpansJson[] filteredResourceSpans)
    {
        var matchingResources = allResources
            .Where(r =>
                string.Equals(r.ResourceName, resourceName, StringComparisons.ResourceName) ||
                string.Equals(OtlpHelpers.GetResourceName(r, allResources), resourceName, StringComparisons.ResourceName))
            .ToArray();

        if (matchingResources.Length == 0)
        {
            filteredResourceSpans = [];
            return false;
        }

        filteredResourceSpans = resourceSpans
            .Where(rs => matchingResources.Any(r =>
                string.Equals(r.ResourceName, rs.Resource?.GetServiceName() ?? "unknown", StringComparisons.ResourceName) &&
                string.Equals(r.InstanceId, rs.Resource?.GetServiceInstanceId(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return true;
    }
}
