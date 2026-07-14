// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
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

internal interface IDashboardResourceEventSource
{
    IAsyncEnumerable<DashboardResourcesEvent> WatchAsync(CancellationToken cancellationToken);
}

internal sealed class DashboardResourceServiceUnavailableException(string message) : Exception(message);

internal sealed class DashboardResourceSnapshotService(
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    ILogger<DashboardResourceSnapshotService> logger) : BackgroundService, IDashboardResourceSnapshotProvider, IDashboardResourceEventSource
{
    private const string ResourceServiceEndpointKey = "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL";
    private const string LegacyResourceServiceEndpointKey = "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL";
    private const string ResourceServiceAuthModeKey = "Dashboard:ResourceServiceClient:AuthMode";
    private const string ResourceServiceApiKeyKey = "Dashboard:ResourceServiceClient:ApiKey";
    private const string InitialSnapshotTimeoutKey = "DashboardBackend:InitialSnapshotTimeout";
    private const string ApiKeyHeaderName = "x-resource-service-api-key";
    private const int ProducerDefinedPropertySortOrderStart = 7;
    private const int SubscriberBufferCapacity = 32;

    private static readonly Dictionary<string, (string DisplayName, int SortOrder)> s_knownProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["resource.displayName"] = ("Display name", 0),
        ["resource.state"] = ("State", 1),
        ["resource.healthState"] = ("Health state", 2),
        ["resource.startTime"] = ("Start time", 3),
        ["resource.stopTime"] = ("Stop time", 4),
        ["resource.exitCode"] = ("Exit code", 5),
        ["resource.connectionString"] = ("Connection string", 6)
    };

    private readonly Dictionary<string, DashboardResource> _resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ChannelWriter<DashboardResourcesEvent>> _subscribers = [];
    private readonly object _lock = new();
    private readonly TaskCompletionSource<bool> _initialStateAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _hasInitialSnapshot;
    private string? _initialFailure;

    public async ValueTask<DashboardResource[]> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await WaitForInitialSnapshotAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            return CreateSnapshotLocked();
        }
    }

    public async IAsyncEnumerable<DashboardResourcesEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await WaitForInitialSnapshotAsync(cancellationToken).ConfigureAwait(false);

        var channel = Channel.CreateBounded<DashboardResourcesEvent>(new BoundedChannelOptions(SubscriberBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        lock (_lock)
        {
            // Add the authoritative snapshot while holding the same lock used for upstream
            // changes. Registering the subscriber before releasing the lock guarantees that
            // no gRPC delta can overtake this first event.
            if (!channel.Writer.TryWrite(DashboardResourcesEvent.Snapshot(CreateSnapshotLocked())))
            {
                throw new InvalidOperationException("The resource subscriber could not accept its initial snapshot.");
            }

            _subscribers.Add(channel.Writer);
        }

        try
        {
            await foreach (var resourceEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return resourceEvent;
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(channel.Writer);
                channel.Writer.TryComplete();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = configuration[ResourceServiceEndpointKey] ?? configuration[LegacyResourceServiceEndpointKey];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var resourceServiceUri))
        {
            ReportInitialFailure($"Configure {ResourceServiceEndpointKey} with the AppHost resource-service endpoint.");
            return;
        }

        var initialSnapshotTimeout = GetInitialSnapshotTimeout();

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
                using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                if (!reconnect)
                {
                    callCancellation.CancelAfter(initialSnapshotTimeout);
                }

                // The resource service sends one complete InitialData frame, followed by
                // upsert/delete Changes frames for the lifetime of this streaming call.
                using var call = client.WatchResources(
                    new WatchResourcesRequest { IsReconnect = reconnect },
                    headers,
                    cancellationToken: callCancellation.Token);
                await foreach (var update in call.ResponseStream.ReadAllAsync(callCancellation.Token).ConfigureAwait(false))
                {
                    ApplyUpdate(update);
                    if (update.KindCase is WatchResourcesUpdate.KindOneofCase.InitialData)
                    {
                        reconnect = true;
                        callCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                }

                if (!reconnect)
                {
                    ReportInitialFailure("The AppHost resource stream ended before providing an initial snapshot. The AOT dashboard backend will keep retrying.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException ex)
            {
                if (!reconnect)
                {
                    ReportInitialFailure($"The AppHost resource service did not provide an initial snapshot within {initialSnapshotTimeout}. The AOT dashboard backend will keep retrying.");
                }

                logger.LogWarning(ex, "The AppHost resource stream disconnected; retrying.");
            }
            catch (RpcException ex)
            {
                if (!reconnect)
                {
                    ReportInitialFailure("The AppHost resource service is unavailable. The AOT dashboard backend will keep retrying.");
                }

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

        TimeSpan GetInitialSnapshotTimeout()
        {
            return TimeSpan.TryParse(
                configuration[InitialSnapshotTimeoutKey],
                CultureInfo.InvariantCulture,
                out var timeout)
                && timeout > TimeSpan.Zero
                    ? timeout
                    : TimeSpan.FromSeconds(10);
        }
    }

    internal void ApplyUpdate(WatchResourcesUpdate update)
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

                _hasInitialSnapshot = true;
                _initialFailure = null;
                _initialStateAvailable.TrySetResult(true);
                PublishLocked(DashboardResourcesEvent.Snapshot(CreateSnapshotLocked()));
                return;
            }

            if (update.KindCase is WatchResourcesUpdate.KindOneofCase.Changes)
            {
                var upserts = new List<DashboardResource>();
                var deletes = new List<string>();
                foreach (var change in update.Changes.Value)
                {
                    if (change.KindCase is WatchResourcesChange.KindOneofCase.Upsert)
                    {
                        var resource = Map(change.Upsert);
                        _resources[resource.Name] = resource;
                        upserts.Add(resource);
                    }
                    else if (change.KindCase is WatchResourcesChange.KindOneofCase.Delete)
                    {
                        _resources.Remove(change.Delete.ResourceName);
                        deletes.Add(change.Delete.ResourceName);
                    }
                }

                PublishLocked(DashboardResourcesEvent.Change([.. upserts], [.. deletes]));
                return;
            }

            throw new FormatException($"Unexpected {nameof(WatchResourcesUpdate)} kind: {update.KindCase}.");
        }
    }

    internal void ReportInitialFailure(string message)
    {
        lock (_lock)
        {
            if (_hasInitialSnapshot)
            {
                return;
            }

            _initialFailure = message;
            _initialStateAvailable.TrySetResult(true);
        }
    }

    private async ValueTask WaitForInitialSnapshotAsync(CancellationToken cancellationToken)
    {
        await _initialStateAvailable.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_hasInitialSnapshot)
            {
                throw new DashboardResourceServiceUnavailableException(
                    _initialFailure ?? "The AppHost resource service has not provided an initial snapshot.");
            }
        }
    }

    private DashboardResource[] CreateSnapshotLocked() =>
        [.. _resources.Values
            .OrderBy(resource => resource.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)];

    private void PublishLocked(DashboardResourcesEvent resourceEvent)
    {
        foreach (var subscriber in _subscribers.ToArray())
        {
            if (!subscriber.TryWrite(resourceEvent))
            {
                _subscribers.Remove(subscriber);
                subscriber.TryComplete(new InvalidOperationException(
                    "The resource subscriber was disconnected because it could not keep up with live changes."));
            }
        }
    }

    internal static DashboardResource Map(ProtoResource resource)
    {
        var properties = resource.Properties
            .Select(MapProperty)
            .OrderBy(property => property.SortOrder)
            .ThenBy(property => property.Name, StringComparer.Ordinal)
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
            [.. resource.Urls.Select(MapUrl).OfType<DashboardResourceUrl>()],
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
                command.HasDisplayDescription && !string.IsNullOrEmpty(command.DisplayDescription) ? command.DisplayDescription : null,
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
        var knownProperty = s_knownProperties.GetValueOrDefault(property.Name);
        var sortOrder = knownProperty != default
            ? knownProperty.SortOrder
            : property.HasSortOrder ? ToProducerDefinedPropertySortOrder(property.SortOrder) : int.MaxValue;

        return new DashboardResourceProperty(
            property.Name,
            property.HasDisplayName ? property.DisplayName : knownProperty.DisplayName,
            property.Value.KindCase is Value.KindOneofCase.StringValue ? property.Value.StringValue : property.Value.ToString(),
            property.HasIsSensitive && property.IsSensitive,
            property.IsHighlighted,
            sortOrder);
    }

    private static DashboardResourceUrl? MapUrl(Url url)
    {
        // The legacy dashboard accepts only absolute URLs and renders them through Uri.ToString(),
        // which canonicalizes an authority-only URL such as http://localhost:5000 with a trailing slash.
        if (!Uri.TryCreate(url.FullUrl, UriKind.Absolute, out var parsedUrl))
        {
            return null;
        }

        return new DashboardResourceUrl(
            url.HasEndpointName ? url.EndpointName : null,
            parsedUrl.ToString(),
            url.IsInternal,
            url.IsInactive,
            string.IsNullOrEmpty(url.DisplayProperties?.DisplayName) ? null : url.DisplayProperties.DisplayName,
            url.DisplayProperties?.SortOrder ?? 0);
    }

    private static int ToProducerDefinedPropertySortOrder(int producerSortOrder)
    {
        if (producerSortOrder <= 0)
        {
            return ProducerDefinedPropertySortOrderStart;
        }

        var sortOrder = ProducerDefinedPropertySortOrderStart + (long)producerSortOrder;
        return sortOrder > int.MaxValue ? int.MaxValue : (int)sortOrder;
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
