// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Runtime;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using ProtoService = Aspire.DashboardService.Proto.V1.DashboardService;

namespace Aspire.Hosting.Native.Dashboard;

/// <summary>Maps scoped native observation capabilities onto the Dashboard gRPC wire contract.</summary>
internal sealed class NativeDashboardService(NativeApplicationServer server, string applicationName, NativeRuntimeOptions options)
    : ProtoService.DashboardServiceBase
{
    public override Task<ApplicationInformationResponse> GetApplicationInformation(
        ApplicationInformationRequest request, ServerCallContext context) =>
        Task.FromResult(new ApplicationInformationResponse
        {
            ApplicationName = applicationName, MinDashboardVersion = "13.5.0"
        });

    public override async Task WatchResources(WatchResourcesRequest request,
        IServerStreamWriter<WatchResourcesUpdate> responseStream, ServerCallContext context)
    {
        ApplicationObserver? previousObserver = null;
        var previous = new Dictionary<string, Resource>(StringComparer.Ordinal);
        long version = -1;
        var initial = true;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var observer = server.GetApplicationObserver();
            ApplicationObservations? observations = null;
            try
            {
                observations = observer?.ReadResourceObservations();
            }
            catch (ObjectDisposedException)
            {
                // Retirement can race an already selected observer. The next poll
                // publishes deletions, then adopts the replacement generation.
                observer = null;
            }
            if (initial || !ReferenceEquals(previousObserver, observer) || observations?.Version != version)
            {
                var current = observations?.Resources.Select(resource => MapResource(resource, observer!.CreatedAt))
                    .ToDictionary(resource => resource.Name, StringComparer.Ordinal) ?? [];
                if (initial)
                {
                    var data = new InitialResourceData();
                    data.Resources.AddRange(current.Values);
                    data.ResourceTypes.AddRange(current.Values.Select(resource => resource.ResourceType)
                        .Distinct(StringComparer.Ordinal).Select(type => new ResourceType { UniqueName = type }));
                    await responseStream.WriteAsync(new WatchResourcesUpdate { InitialData = data },
                        context.CancellationToken).ConfigureAwait(false);
                    initial = false;
                }
                else
                {
                    var changes = new WatchResourcesChanges();
                    foreach (var resource in previous.Values.Where(resource =>
                        !ReferenceEquals(previousObserver, observer) || !current.ContainsKey(resource.Name)))
                    {
                        changes.Value.Add(new WatchResourcesChange
                        {
                            Delete = new ResourceDeletion { ResourceName = resource.Name, ResourceType = resource.ResourceType }
                        });
                    }
                    changes.Value.AddRange(current.Values.Where(resource =>
                        !previous.TryGetValue(resource.Name, out var old) || !old.Equals(resource))
                        .Select(resource => new WatchResourcesChange { Upsert = resource }));
                    if (changes.Value.Count > 0)
                    {
                        await responseStream.WriteAsync(new WatchResourcesUpdate { Changes = changes },
                            context.CancellationToken).ConfigureAwait(false);
                    }
                }
                previous = current;
                previousObserver = observer;
                version = observations?.Version ?? -1;
            }
            await Task.Delay(options.ObservationInterval, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task WatchResourceConsoleLogs(WatchResourceConsoleLogsRequest request,
        IServerStreamWriter<WatchResourceConsoleLogsUpdate> responseStream, ServerCallContext context)
    {
        var observer = RequireObserver();
        var resource = observer.ReadResourceObservations().Resources.SingleOrDefault(resource => resource.Name == request.ResourceName)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "The resource does not exist."));
        long sequence = 0;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            if (observer.IsRevoked)
            {
                return;
            }
            ResourceLogs batch;
            try
            {
                batch = observer.ReadResourceLogs(resource.ResourceId, sequence);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (batch.Entries.Length > 0)
            {
                var update = new WatchResourceConsoleLogsUpdate();
                // The kernel retains at most 256 entries. Send each line separately
                // so UTF-8 expansion cannot push a retained batch over gRPC's limit.
                foreach (var line in batch.Entries)
                {
                    update.LogLines.Add(new ConsoleLogLine
                    {
                        Text = line.Message, IsStdErr = line.Stream == "stderr",
                        LineNumber = checked((int)line.Sequence)
                    });
                    await responseStream.WriteAsync(update, context.CancellationToken).ConfigureAwait(false);
                    update.LogLines.Clear();
                }
                sequence = batch.LastSequence;
            }
            if (request.SuppressFollow)
            {
                return;
            }
            await Task.Delay(options.ObservationInterval, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<ResourceCommandResponse> ExecuteResourceCommand(
        Aspire.DashboardService.Proto.V1.ResourceCommandRequest request, ServerCallContext context)
    {
        if (request.Arguments.Count > 0)
        {
            return new ResourceCommandResponse
            {
                Kind = ResourceCommandResponseKind.InvalidArguments, Message = "This native command does not accept arguments."
            };
        }
        var observer = RequireObserver();
        var resource = observer.ReadResourceObservations().Resources.SingleOrDefault(resource =>
            resource.Name == request.ResourceName && resource.TypeId == request.ResourceType);
        if (resource is null || !resource.Commands.Any(command => command.Name == request.CommandName))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "The resource command does not exist."));
        }
        var operation = observer.InvokeResourceCommand(request.ResourceName, request.CommandName);
        var result = await operation.AwaitRuntimeOperation().WaitAsync(context.CancellationToken).ConfigureAwait(false);

        return new ResourceCommandResponse
        {
            Kind = result.Status switch
            {
                "succeeded" => ResourceCommandResponseKind.Succeeded,
                "failed" => ResourceCommandResponseKind.Failed,
                "cancelled" => ResourceCommandResponseKind.Cancelled,
                _ => throw new InvalidOperationException("Unknown native command outcome.")
            },
            Message = result.Message
        };
    }

    public override async Task WatchInteractions(IAsyncStreamReader<WatchInteractionsRequestUpdate> requestStream,
        IServerStreamWriter<WatchInteractionsResponseUpdate> responseStream, ServerCallContext context)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        // Interaction IDs are only meaningful within this stream. Retaining the
        // original observer prevents an old reply from acquiring replacement authority.
        var pending = new Dictionary<int, (ApplicationObserver Observer, string RequestId)>();
        var gate = new Lock();
        var nextId = 0;
        var send = SendAsync();
        try
        {
            await foreach (var request in requestStream.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                (ApplicationObserver Observer, string RequestId) prompt;
                lock (gate)
                {
                    if (!pending.TryGetValue(request.InteractionId, out prompt))
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "The interaction is not pending on this stream."));
                    }
                }
                if (request.KindCase != WatchInteractionsRequestUpdate.KindOneofCase.MessageBox || !request.MessageBox.HasResult)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "A confirmation response is required."));
                }
                if (!prompt.Observer.IsRevoked)
                {
                    try
                    {
                        prompt.Observer.RespondConfirmation(prompt.RequestId, request.MessageBox.Result);
                    }
                    catch (Exception exception) when (exception is ObjectDisposedException or ArgumentException)
                    {
                        throw new RpcException(new Status(StatusCode.FailedPrecondition, "The confirmation was retired or answered."));
                    }
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await send.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        async Task SendAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var observer = server.GetApplicationObserver();
                    ConfirmationRequest[] requests;
                    try
                    {
                        requests = observer?.ReadConfirmations().Requests ?? [];
                    }
                    catch (ObjectDisposedException)
                    {
                        requests = [];
                        observer = null;
                    }
                    var updates = new List<WatchInteractionsResponseUpdate>();
                    lock (gate)
                    {
                        foreach (var entry in pending.ToArray())
                        {
                            if (entry.Value.Observer.IsRevoked || !ReferenceEquals(entry.Value.Observer, observer) ||
                                !requests.Any(request => request.RequestId == entry.Value.RequestId))
                            {
                                pending.Remove(entry.Key);
                                updates.Add(new WatchInteractionsResponseUpdate
                                {
                                    InteractionId = entry.Key, Complete = new InteractionComplete()
                                });
                            }
                        }
                        foreach (var request in requests.Where(request =>
                            !pending.Values.Any(prompt => ReferenceEquals(prompt.Observer, observer) && prompt.RequestId == request.RequestId)))
                        {
                            var id = checked(++nextId);
                            pending.Add(id, (observer!, request.RequestId));
                            updates.Add(new WatchInteractionsResponseUpdate
                            {
                                InteractionId = id, Title = request.ResourceName, Message = request.Message,
                                PrimaryButtonText = "Continue", SecondaryButtonText = "Cancel",
                                ShowDismiss = true, ShowSecondaryButton = true, MessageBox = new InteractionMessageBox()
                            });
                        }
                    }
                    foreach (var update in updates)
                    {
                        await responseStream.WriteAsync(update, lifetime.Token).ConfigureAwait(false);
                    }
                    await Task.Delay(options.ObservationInterval, lifetime.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    public override async Task WatchTerminals(WatchTerminalsRequest request,
        IServerStreamWriter<WatchTerminalsUpdate> responseStream, ServerCallContext context)
    {
        // The Dashboard subscribes unconditionally. An empty initial inventory
        // is truthful for this runtime slice; attach/close remain Unimplemented.
        await responseStream.WriteAsync(new WatchTerminalsUpdate { Snapshot = new TerminalDescriptorList() },
            context.CancellationToken).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken).ConfigureAwait(false);
    }

    private ApplicationObserver RequireObserver() => server.GetApplicationObserver()
        ?? throw new RpcException(new Status(StatusCode.Unavailable, "No native application execution is active."));

    private static Resource MapResource(ResourceObservationSnapshot snapshot, DateTimeOffset createdAt)
    {
        var resource = new Resource
        {
            Name = snapshot.Name, DisplayName = snapshot.Name, Uid = snapshot.ResourceId,
            ResourceType = snapshot.TypeId, State = snapshot.State, CreatedAt = Timestamp.FromDateTimeOffset(createdAt),
            StateStyle = snapshot.State switch { "Running" => "success", "Failed" => "error", _ => "info" }
        };
        resource.Urls.AddRange(snapshot.Urls.Select(url => new Url
        {
            FullUrl = url, DisplayProperties = new UrlDisplayProperties { DisplayName = url }
        }));
        resource.Commands.AddRange(snapshot.Commands.Select(command => new ResourceCommand
        {
            Name = command.Name, DisplayName = command.DisplayName, State = ResourceCommandState.Enabled
        }));
        resource.HealthReports.Add(new HealthReport
        {
            Key = "native.integration", Status = snapshot.Healthy ? HealthStatus.Healthy : HealthStatus.Unhealthy
        });

        return resource;
    }
}
