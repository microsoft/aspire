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
    private const string ServiceInstanceIdAttributeName = "service.instance.id";

    // Describe and list_resources are interactive, one-shot reads. Bound the extra dashboard request so an
    // unresponsive dashboard degrades to "status unknown" instead of delaying the resource listing.
    private static readonly TimeSpan s_queryTimeout = TimeSpan.FromSeconds(5);

    // Composite names ("{service.name}-{service.instance.id}", or "{service.name}") of telemetry sources.
    private readonly HashSet<string> _compositeNames = new(StringComparers.ResourceName);
    private readonly HashSet<string> _namesWithoutInstanceId = new(StringComparers.ResourceName);

    private ResourceTelemetryStatus(IEnumerable<ResourceInfoJson> telemetryResources)
    {
        foreach (var resource in telemetryResources)
        {
            _compositeNames.Add(GetCompositeName(resource.Name, resource.InstanceId));
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
    internal static bool IsOtlpExporterConfigured(ResourceSnapshot snapshot) =>
        // An explicitly empty endpoint disables export; a null value means the value wasn't captured.
        snapshot.EnvironmentVariables.Any(e =>
            string.Equals(e.Name, KnownOtelConfigNames.ExporterOtlpEndpoint, StringComparison.Ordinal) &&
            e.Value is not { Length: 0 });

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

        // The dashboard keys telemetry by the service.name and service.instance.id the app reports. Prefer
        // the values the resource was started with: they differ from the runtime resource name for persistent
        // resources (whose instance ID is the full runtime name) and when the app overrides them.
        var serviceName = GetResolvedEnvironmentValue(snapshot, KnownOtelConfigNames.ServiceName);
        var instanceId = GetServiceInstanceId(snapshot);
        if (serviceName is not null && instanceId is not null && _compositeNames.Contains(GetCompositeName(serviceName, instanceId)))
        {
            return true;
        }

        // Fall back to the runtime name, which is the composite name for session-lifetime resources. An app that
        // keeps service.name but drops service.instance.id is reported without an instance ID.
        return _compositeNames.Contains(snapshot.Name) ||
            ((serviceName ?? snapshot.DisplayName) is { } name && _namesWithoutInstanceId.Contains(name));
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
            if (dashboardInfo?.ApiToken is not { } apiToken ||
                DashboardUrls.NormalizeDashboardRequestUrl(dashboardInfo.ApiBaseUrl, stripLoginPath: false) is not { } apiBaseUrl)
            {
                logger.LogDebug("Dashboard telemetry API is not available; resource telemetry status is unknown.");
                return null;
            }

            using var client = TelemetryCommandHelpers.CreateApiClient(httpClientFactory, apiToken);
            return Create(await TelemetryCommandHelpers.GetAllResourcesAsync(client, apiBaseUrl, timeoutCts.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The status is supplementary: a missing, older, slow, or failing dashboard must not fail the listing.
            logger.LogDebug(ex, "Unable to read resource telemetry status from the dashboard.");
            return null;
        }
    }

    private static string GetCompositeName(string name, string? instanceId) =>
        instanceId is null ? name : $"{name}-{instanceId}";

    private static string? GetResolvedEnvironmentValue(ResourceSnapshot snapshot, string name)
    {
        var value = snapshot.EnvironmentVariables
            .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))?.Value;

        // Before DCP reports the effective environment, the snapshot carries the spec value, which is an
        // unresolved DCP template such as {{- index .Annotations "otel-service-name" -}}.
        return string.IsNullOrEmpty(value) || value.Contains("{{", StringComparison.Ordinal) ? null : value;
    }

    private static string? GetServiceInstanceId(ResourceSnapshot snapshot)
    {
        // OTEL_RESOURCE_ATTRIBUTES is a comma-separated list of percent-encoded key=value pairs, for example
        //   deployment.environment=dev,service.instance.id=worker-x7k2
        // https://opentelemetry.io/docs/specs/otel/resource/sdk/#specifying-resource-information-via-an-environment-variable
        if (GetResolvedEnvironmentValue(snapshot, KnownOtelConfigNames.ResourceAttributes) is not { } attributes)
        {
            return null;
        }

        foreach (var attribute in attributes.Split(',', StringSplitOptions.TrimEntries))
        {
            var separator = attribute.IndexOf('=');
            if (separator > 0 &&
                string.Equals(attribute[..separator].Trim(), ServiceInstanceIdAttributeName, StringComparison.Ordinal) &&
                Uri.UnescapeDataString(attribute[(separator + 1)..].Trim()) is { Length: > 0 } instanceId)
            {
                return instanceId;
            }
        }

        return null;
    }
}
