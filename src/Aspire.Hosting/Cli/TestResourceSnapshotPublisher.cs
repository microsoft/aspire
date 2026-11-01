// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.Hosting.Cli;

internal sealed class TestResourceSnapshotPublisher(DistributedApplicationModel model, ResourceNotificationService notifications)
{
    private readonly Channel<TestRunEvent> _updates = Channel.CreateUnbounded<TestRunEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, ResourceResults> _results = new(StringComparers.ResourceName);
    private Task? _publisher;

    public void Start() => _publisher = PublishAsync();

    public void Publish(TestRunEvent item)
    {
        if (item.Result is not null || item.Collection is not null || item.Resource is { ResourceId: not null } ||
            item.Resource?.State == KnownResourceStates.Running)
        {
            if (!_updates.Writer.TryWrite(item))
            {
                throw new InvalidOperationException("Testing snapshots have already completed.");
            }
        }
    }

    public async Task CompleteAsync()
    {
        _updates.Writer.TryComplete();
        if (_publisher is not null)
        {
            // Drain final results before CLI completion, without tying data retention to a
            // dashboard subscriber. Resource snapshots replay these values after reconnect.
            await _publisher.ConfigureAwait(false);
        }
    }

    private async Task PublishAsync()
    {
        await foreach (var item in _updates.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var name = item.Result?.ResourceName ?? item.Collection?.ResourceName ?? item.Resource!.ResourceName;
            if (!_results.TryGetValue(name, out var state))
            {
                _results[name] = state = new ResourceResults();
            }
            if (item.Result is { } result)
            {
                var fields = new Struct();
                fields.Fields["name"] = Value.ForString(result.Name);
                fields.Fields["outcome"] = Value.ForString(result.Outcome);
                if (result.DurationMilliseconds is { } duration)
                {
                    fields.Fields["durationMilliseconds"] = Value.ForNumber(duration);
                }
                if (result.Message is { } message)
                {
                    fields.Fields["message"] = Value.ForString(message);
                }
                state.Results.Add(new Value { StructValue = fields });
            }
            if (item.Collection is { } collection)
            {
                state.Collection = collection;
            }
            if (item.Resource is { } resource)
            {
                var added = state.ResourceIds.Add(resource.ResourceId ?? name);
                state.ExpectedReplicas = resource.ExpectedReplicas;
                if (resource.Outcome is "passed" or "failed")
                {
                    state.Outcomes[resource.ResourceId ?? name] = resource.Outcome;
                }
                else if (!added || state.Collection is null && state.Results.Count == 0)
                {
                    // Existing observations need no update. This also breaks the feedback loop
                    // from our own notification writes. New instances receive retained results.
                    continue;
                }
            }

            // Contract example: { results: [{name:"probe", outcome:"passed"}],
            // collectionStatus:"completed", resourceOutcome:"failed" }. Results belong to
            // the logical resource; replica outcomes remain aggregated separately.
            var snapshot = new Struct();
            snapshot.Fields["revision"] = Value.ForNumber(++state.Revision);
            snapshot.Fields["results"] = Value.ForList(state.Results.ToArray());
            snapshot.Fields["collectionStatus"] = Value.ForString(state.Collection?.Status ?? "pending");
            snapshot.Fields["resourceOutcome"] = Value.ForString(state.Outcomes.Values.Any(o => o == "failed") ? "failed"
                : state.Outcomes.Count >= state.ExpectedReplicas ? "passed" : "pending");
            if (state.Collection?.Error is { } error)
            {
                snapshot.Fields["collectionError"] = Value.ForString(error);
            }
            var value = new Value { StructValue = snapshot };
            var appResource = model.Resources.Single(r => StringComparers.ResourceName.Equals(r.Name, name));
#pragma warning disable ASPIRETESTING001 // Retain collected data in the existing annotation for fresh producer snapshots.
            if (appResource.TryGetLastAnnotation<TestingAnnotation>(out var testing))
            {
                testing.DashboardResults = value;
            }
#pragma warning restore ASPIRETESTING001
            // Do not create a logical placeholder before DCP resolves a project's real identity.
            // Annotation projection covers first snapshots; observations identify the existing
            // instances that need immediate updates when collectors publish new results.
            foreach (var id in state.ResourceIds)
            {
                if (notifications.TryGetCurrentState(id, out var current) && !current.Snapshot.IsHidden)
                {
                    await notifications.PublishUpdateAsync(appResource, id, s => s with
                    {
                        Properties = s.Properties.Where(p => p.Name != KnownProperties.Resource.TestingResults).ToImmutableArray()
                            .Add(new ResourcePropertySnapshot(KnownProperties.Resource.TestingResults, value))
                    }).ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class ResourceResults
    {
        public List<Value> Results { get; } = [];
        public HashSet<string> ResourceIds { get; } = new(StringComparers.ResourceName);
        public Dictionary<string, string> Outcomes { get; } = new(StringComparers.ResourceName);
        public int ExpectedReplicas { get; set; } = 1;
        public int Revision { get; set; }
        public TestCollectionState? Collection { get; set; }
    }
}
