// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Aspire.Otlp.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Tells whether resources whose AppHost configured OTLP export have sent any telemetry to the dashboard.
/// </summary>
/// <remarks>
/// The AppHost sets <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and related variables on resources, but only an
/// OpenTelemetry SDK running inside the app consumes them. A JavaScript app without an SDK, for example,
/// receives the variables and never exports anything. Surfacing "OTLP configured, nothing received" lets
/// agents and people tell such a resource apart from one whose telemetry is merely empty.
/// </remarks>
internal sealed class ResourceTelemetryStatus
{
    // Describe and list_resources are interactive, one-shot reads. Bound the extra dashboard request so an
    // unresponsive dashboard degrades to "status unknown" instead of delaying the resource listing.
    private static readonly TimeSpan s_queryTimeout = TimeSpan.FromSeconds(5);

    private readonly HashSet<string> _compositeNames;
    private readonly HashSet<string> _namesWithoutInstanceId;

    private ResourceTelemetryStatus(IEnumerable<ResourceInfoJson> telemetryResources)
    {
        _compositeNames = new HashSet<string>(StringComparers.ResourceName);
        _namesWithoutInstanceId = new HashSet<string>(StringComparers.ResourceName);

        foreach (var resource in telemetryResources)
        {
            // The dashboard reports each telemetry source as service.name plus service.instance.id, and its
            // composite name ("{name}-{instanceId}", or just "{name}") matches the AppHost's runtime resource
            // name because the AppHost sets both attributes from that name. This is the same mapping the
            // dashboard uses to attach telemetry to app model resources.
            var compositeName = resource.DisplayName is { Length: > 0 } displayName
                ? displayName
                : resource.InstanceId is null ? resource.Name : $"{resource.Name}-{resource.InstanceId}";
            _compositeNames.Add(compositeName);

            if (resource.InstanceId is null)
            {
                _namesWithoutInstanceId.Add(resource.Name);
            }
        }
    }

    /// <summary>
    /// Creates a status from the resources reported by the dashboard telemetry API.
    /// </summary>
    internal static ResourceTelemetryStatus Create(IEnumerable<ResourceInfoJson> telemetryResources) => new(telemetryResources);

    /// <summary>
    /// Gets whether the AppHost configured an OTLP exporter endpoint for the resource.
    /// </summary>
    internal static bool IsOtlpExporterConfigured(ResourceSnapshot snapshot)
    {
        foreach (var environmentVariable in snapshot.EnvironmentVariables)
        {
            // An explicitly empty endpoint disables export; a null value means the value wasn't captured.
            if (string.Equals(environmentVariable.Name, KnownOtelConfigNames.ExporterOtlpEndpoint, StringComparison.Ordinal) &&
                environmentVariable.Value is not { Length: 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether the dashboard has received telemetry from the resource.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the AppHost didn't configure OTLP export for the resource, so receiving
    /// nothing is expected; otherwise whether any telemetry from the resource reached the dashboard.
    /// </returns>
    internal bool? GetTelemetryReceived(ResourceSnapshot snapshot)
    {
        if (!IsOtlpExporterConfigured(snapshot))
        {
            return null;
        }

        // An app that keeps service.name but drops service.instance.id is reported without an instance ID.
        // Attribute that telemetry to the resource rather than claiming nothing was received.
        return _compositeNames.Contains(snapshot.Name) ||
            (snapshot.DisplayName is { } displayName && _namesWithoutInstanceId.Contains(displayName));
    }

    /// <summary>
    /// Reads which resources have sent telemetry from the dashboard of the given AppHost.
    /// </summary>
    /// <returns>
    /// The telemetry status, or <see langword="null"/> when no snapshot has OTLP configured or the dashboard
    /// telemetry API is unavailable, unreachable, or doesn't respond within a short timeout.
    /// </returns>
    internal static async Task<ResourceTelemetryStatus?> TryGetAsync(
        IAppHostAuxiliaryBackchannel connection,
        IEnumerable<ResourceSnapshot> snapshots,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!snapshots.Any(IsOtlpExporterConfigured))
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_queryTimeout);

        try
        {
            var dashboardInfo = await connection.GetDashboardInfoV2Async(timeoutCts.Token).ConfigureAwait(false);
            if (dashboardInfo?.ApiBaseUrl is null || dashboardInfo.ApiToken is null)
            {
                logger.LogDebug("Dashboard telemetry API is not available; resource telemetry status is unknown.");
                return null;
            }

            var apiBaseUrl = DashboardUrls.NormalizeDashboardRequestUrl(dashboardInfo.ApiBaseUrl, stripLoginPath: false);
            if (apiBaseUrl is null)
            {
                return null;
            }

            using var client = TelemetryCommandHelpers.CreateApiClient(httpClientFactory, dashboardInfo.ApiToken);
            var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, apiBaseUrl, timeoutCts.Token).ConfigureAwait(false);
            return Create(resources);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Timed out reading resource telemetry status from the dashboard.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The status is supplementary: a missing, older, or failing dashboard must not fail the listing.
            logger.LogDebug(ex, "Unable to read resource telemetry status from the dashboard.");
            return null;
        }
    }
}
