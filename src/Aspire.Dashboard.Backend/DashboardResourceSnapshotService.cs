// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using ProtoResource = Aspire.DashboardService.Proto.V1.Resource;

namespace Aspire.Dashboard.Backend;

internal interface IDashboardResourceSnapshotProvider
{
    ValueTask<DashboardResource[]> GetSnapshotAsync(CancellationToken cancellationToken);
}

internal sealed class DashboardResourceServiceUnavailableException(string message) : Exception(message);

internal sealed class DashboardResourceSnapshotService(
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    ILogger<DashboardResourceSnapshotService> logger) : BackgroundService, IDashboardResourceSnapshotProvider
{
    private const string ResourceServiceEndpointKey = "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL";
    private const string LegacyResourceServiceEndpointKey = "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL";
    private const string ResourceServiceAuthModeKey = "Dashboard:ResourceServiceClient:AuthMode";
    private const string ResourceServiceApiKeyKey = "Dashboard:ResourceServiceClient:ApiKey";
    private const string ApiKeyHeaderName = "x-resource-service-api-key";

    private readonly Dictionary<string, DashboardResource> _resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly TaskCompletionSource<bool> _initialSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<DashboardResource[]> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _initialSnapshot.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            return [.. _resources.Values.OrderBy(resource => resource.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = configuration[ResourceServiceEndpointKey] ?? configuration[LegacyResourceServiceEndpointKey];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var resourceServiceUri))
        {
            _initialSnapshot.TrySetException(new DashboardResourceServiceUnavailableException(
                $"Configure {ResourceServiceEndpointKey} with the AppHost resource-service endpoint."));
            return;
        }

        using var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
        };
        using var channel = GrpcChannel.ForAddress(resourceServiceUri, new GrpcChannelOptions
        {
            HttpHandler = handler,
            LoggerFactory = loggerFactory,
            ThrowOperationCanceledOnCancellation = true,
            MaxReceiveMessageSize = 16 * 1024 * 1024
        });
        var client = new Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient(channel);
        var headers = CreateHeaders();
        var reconnect = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The resource service sends one complete InitialData frame, followed by
                // upsert/delete Changes frames for the lifetime of this streaming call.
                using var call = client.WatchResources(
                    new WatchResourcesRequest { IsReconnect = reconnect },
                    headers,
                    cancellationToken: stoppingToken);
                await foreach (var update in call.ResponseStream.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    ApplyUpdate(update);
                    reconnect = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex)
            {
                logger.LogWarning(ex, "The AppHost resource stream disconnected; retrying.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }

        Metadata CreateHeaders()
        {
            var metadata = new Metadata();
            if (string.Equals(configuration[ResourceServiceAuthModeKey], "ApiKey", StringComparison.OrdinalIgnoreCase)
                && configuration[ResourceServiceApiKeyKey] is { Length: > 0 } apiKey)
            {
                metadata.Add(ApiKeyHeaderName, apiKey);
            }

            return metadata;
        }
    }

    private void ApplyUpdate(WatchResourcesUpdate update)
    {
        lock (_lock)
        {
            if (update.KindCase is WatchResourcesUpdate.KindOneofCase.InitialData)
            {
                _resources.Clear();
                foreach (var resource in update.InitialData.Resources)
                {
                    _resources[resource.Name] = Map(resource);
                }

                _initialSnapshot.TrySetResult(true);
                return;
            }

            if (update.KindCase is WatchResourcesUpdate.KindOneofCase.Changes)
            {
                foreach (var change in update.Changes.Value)
                {
                    if (change.KindCase is WatchResourcesChange.KindOneofCase.Upsert)
                    {
                        _resources[change.Upsert.Name] = Map(change.Upsert);
                    }
                    else if (change.KindCase is WatchResourcesChange.KindOneofCase.Delete)
                    {
                        _resources.Remove(change.Delete.ResourceName);
                    }
                }

                return;
            }

            throw new FormatException($"Unexpected {nameof(WatchResourcesUpdate)} kind: {update.KindCase}.");
        }
    }

    internal static DashboardResource Map(ProtoResource resource)
    {
        var properties = resource.Properties
            .OrderBy(property => property.HasSortOrder ? property.SortOrder : int.MaxValue)
            .ThenBy(property => property.Name, StringComparer.Ordinal)
            .Select(MapProperty)
            .ToArray();
        var terminalReplicaIndex = TryGetPropertyString(resource, "terminal.replicaIndex", out var replicaIndexText)
            && int.TryParse(replicaIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var replicaIndex)
            ? replicaIndex
            : (int?)null;
        var hasTerminal = resource.Properties.Any(property => string.Equals(property.Name, "terminal.enabled", StringComparison.OrdinalIgnoreCase))
            && terminalReplicaIndex.HasValue;

        return new DashboardResource(
            resource.Name,
            resource.ResourceType,
            resource.DisplayName,
            resource.Uid,
            resource.HasState ? resource.State : null,
            resource.HasStateStyle ? resource.StateStyle : null,
            GetHealth(resource),
            resource.CreatedAt?.ToDateTime(),
            resource.StartedAt?.ToDateTime(),
            resource.StoppedAt?.ToDateTime(),
            [.. resource.Urls.Select(url => new DashboardResourceUrl(
                url.HasEndpointName ? url.EndpointName : null,
                url.FullUrl,
                url.IsInternal,
                url.IsInactive,
                string.IsNullOrEmpty(url.DisplayProperties?.DisplayName) ? null : url.DisplayProperties.DisplayName,
                url.DisplayProperties?.SortOrder ?? 0))],
            properties,
            [.. resource.Environment.Select(environment => new DashboardEnvironmentVariable(
                environment.Name,
                environment.HasValue ? environment.Value : null,
                environment.IsFromSpec))],
            [.. resource.HealthReports.OrderBy(report => report.Key, StringComparer.Ordinal).Select(report => new DashboardHealthReport(
                report.HasStatus ? MapHealthStatus(report.Status) : null,
                report.Key,
                report.Description))],
            [.. resource.Commands.Select(command => new DashboardResourceCommand(
                command.Name,
                command.DisplayName,
                command.HasDisplayDescription ? command.DisplayDescription : null,
                command.HasConfirmationMessage && !string.IsNullOrEmpty(command.ConfirmationMessage) ? command.ConfirmationMessage : null,
                command.HasIconName && !string.IsNullOrEmpty(command.IconName) ? command.IconName : null,
                MapIconVariant(command.IconVariant),
                command.IsHighlighted,
                MapCommandState(command.State)))],
            [.. resource.Relationships.Select(relationship => new DashboardResourceRelationship(relationship.ResourceName, relationship.Type))],
            resource.IsHidden || string.Equals(resource.State, "Hidden", StringComparison.OrdinalIgnoreCase),
            resource.SupportsDetailedTelemetry,
            resource.HasIconName ? resource.IconName : null,
            resource.HasIconVariant ? MapIconVariant(resource.IconVariant) : null,
            hasTerminal,
            hasTerminal ? terminalReplicaIndex : null);
    }

    private static DashboardResourceProperty MapProperty(ResourceProperty property)
    {
        return new DashboardResourceProperty(
            property.Name,
            property.HasDisplayName ? property.DisplayName : null,
            property.Value.KindCase is Value.KindOneofCase.StringValue ? property.Value.StringValue : property.Value.ToString(),
            property.HasIsSensitive && property.IsSensitive,
            property.IsHighlighted,
            property.HasSortOrder ? property.SortOrder : null);
    }

    private static string? GetHealth(ProtoResource resource)
    {
        if (!string.Equals(resource.State, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (resource.HealthReports.Count is 0)
        {
            return "Healthy";
        }

        if (resource.HealthReports.Any(report => !report.HasStatus || report.Status is HealthStatus.Unhealthy))
        {
            return "Unhealthy";
        }

        return resource.HealthReports.Any(report => report.Status is HealthStatus.Degraded)
            ? "Degraded"
            : "Healthy";
    }

    private static bool TryGetPropertyString(ProtoResource resource, string name, out string? value)
    {
        var property = resource.Properties.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (property?.Value.KindCase is Value.KindOneofCase.StringValue)
        {
            value = property.Value.StringValue;
            return true;
        }

        value = null;
        return false;
    }

    private static string MapHealthStatus(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "Healthy",
        HealthStatus.Degraded => "Degraded",
        HealthStatus.Unhealthy => "Unhealthy",
        _ => throw new InvalidOperationException($"Unexpected {nameof(HealthStatus)} value: {status}.")
    };

    private static string MapIconVariant(IconVariant variant) => variant switch
    {
        IconVariant.Regular => "regular",
        IconVariant.Filled => "filled",
        _ => throw new InvalidOperationException($"Unexpected {nameof(IconVariant)} value: {variant}.")
    };

    private static string MapCommandState(ResourceCommandState state) => state switch
    {
        ResourceCommandState.Enabled => "enabled",
        ResourceCommandState.Disabled => "disabled",
        ResourceCommandState.Hidden => "hidden",
        _ => throw new InvalidOperationException($"Unexpected {nameof(ResourceCommandState)} value: {state}.")
    };
}
