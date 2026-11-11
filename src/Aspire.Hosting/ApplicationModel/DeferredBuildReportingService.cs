// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

internal sealed class DeferredBuildReportingService(ResourceNotificationService notifications)
{
    internal const string BeginReportingKey = "Aspire.Hosting.DeferredBuildReporting.Begin";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ActiveState> _activeStates = new(StringComparers.ResourceName);

    public async Task<IAsyncDisposable> BeginAsync(BeforeResourceStartedEvent @event)
    {
        var scope = new ReportingScope(this, @event.Resource);
        var resourceIds = @event.ResourceInstanceId is { } resourceId
            ? [resourceId]
            : @event.Resource.TryGetInstances(out var instances)
                ? instances.Select(instance => instance.Name)
                : [@event.Resource.Name];

        try
        {
            foreach (var id in resourceIds)
            {
                await notifications.PublishUpdateAsync(@event.Resource, id, snapshot =>
                {
                    lock (_lock)
                    {
                        if (_activeStates.TryGetValue(id, out var active) && ReferenceEquals(snapshot.State, active.BuildingState))
                        {
                            active.WaiterCount++;
                            scope.Add(id, active);
                            return snapshot;
                        }

                        // Moving a Waiting replica to Building would release its dependency gate.
                        if (snapshot.State?.Text != KnownResourceStates.Starting)
                        {
                            return snapshot;
                        }

                        active = new ActiveState(snapshot.State);
                        _activeStates[id] = active;
                        scope.Add(id, active);
                        return snapshot with { State = active.BuildingState };
                    }
                }).ConfigureAwait(false);
            }

            return scope;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public CustomResourceSnapshot ApplyDcpSnapshot(string resourceId, CustomResourceSnapshot current, CustomResourceSnapshot updated)
    {
        lock (_lock)
        {
            if (_activeStates.TryGetValue(resourceId, out var active) &&
                ReferenceEquals(current.State, active.BuildingState) &&
                (updated.State?.Text is null ||
                 updated.State.Text == KnownResourceStates.NotStarted ||
                 updated.State.Text == KnownResourceStates.Starting))
            {
                // DCP can repeat the pre-start snapshot while the start callback is waiting for a build.
                return updated with { State = active.BuildingState };
            }
        }

        return updated;
    }

    private async ValueTask EndAsync(IResource resource, IReadOnlyList<(string Id, ActiveState State)> ownedStates)
    {
        foreach (var (id, owned) in ownedStates)
        {
            await notifications.PublishUpdateAsync(resource, id, snapshot =>
            {
                lock (_lock)
                {
                    if (!_activeStates.TryGetValue(id, out var active) || !ReferenceEquals(active, owned))
                    {
                        return snapshot;
                    }

                    if (--active.WaiterCount > 0)
                    {
                        return snapshot;
                    }

                    _activeStates.Remove(id);
                    return ReferenceEquals(snapshot.State, active.BuildingState)
                        ? snapshot with { State = active.PreviousState }
                        : snapshot;
                }
            }).ConfigureAwait(false);
        }
    }

    private sealed class ActiveState(ResourceStateSnapshot? previousState)
    {
        public ResourceStateSnapshot? PreviousState { get; } = previousState;
        public ResourceStateSnapshot BuildingState { get; } = new(KnownResourceStates.Building, KnownResourceStateStyles.Info);
        public int WaiterCount { get; set; } = 1;
    }

    private sealed class ReportingScope(DeferredBuildReportingService owner, IResource resource) : IAsyncDisposable
    {
        private readonly List<(string Id, ActiveState State)> _ownedStates = [];
        private int _disposed;

        public void Add(string id, ActiveState state) => _ownedStates.Add((id, state));

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
            ? owner.EndAsync(resource, _ownedStates)
            : ValueTask.CompletedTask;
    }
}
