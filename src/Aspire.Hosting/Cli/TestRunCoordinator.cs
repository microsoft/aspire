// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aspire.Hosting.Cli;

/// <summary>
/// Observes test resources without changing the application's lifetime.
/// </summary>
internal sealed class TestRunCoordinator(
    DistributedApplicationModel model,
    ResourceNotificationService notifications,
    TimeProvider timeProvider,
    ILogger<TestRunCoordinator> logger)
{
    internal static readonly TimeSpan s_exitCodeTimeout = TimeSpan.FromSeconds(5);

    public async Task<bool> WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await WaitForCompletionAsync(GetTestResources(), onCompleted: null, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TestRunEvent> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<TestRunEvent>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(channel.Writer, stop.Token);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            stop.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(ChannelWriter<TestRunEvent> writer, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? observations = null;
        Task<bool>? completion = null;
        Exception? failure = null;
        bool? passed = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resources = GetTestResources();
            foreach (var resource in resources)
            {
                writer.TryWrite(new TestRunEvent
                {
                    Resource = new TestResourceState
                    {
                        ResourceName = resource.Name,
                        State = KnownResourceStates.NotStarted,
                        ExpectedReplicas = resource.GetReplicaCount()
                    }
                });
            }

            observations = ObserveAsync(resources, writer, stop.Token);
            completion = WaitForCompletionAsync(resources,
                result => writer.TryWrite(new TestRunEvent { Resource = result }), stop.Token);
            if (await Task.WhenAny(observations, completion).ConfigureAwait(false) == observations)
            {
                await observations.ConfigureAwait(false);
                throw new DistributedApplicationException("The test resource notification stream ended before test completion.");
            }

            passed = await completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            stop.Cancel();
            // Drain both producers before publishing completion. Final outcomes are explicitly
            // queued by the waiters, so cancellation of observation cannot lose terminal snapshots.
            foreach (var task in new Task?[] { observations, completion })
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
            }
        }

        if (failure is null)
        {
            writer.TryWrite(new TestRunEvent { Passed = passed });
        }
        writer.TryComplete(failure);
    }

    private async Task ObserveAsync(IResource[] resources, ChannelWriter<TestRunEvent> writer, CancellationToken cancellationToken)
    {
        var names = resources.ToDictionary(r => r.Name, StringComparers.ResourceName);
        await foreach (var item in notifications.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            if (names.TryGetValue(item.Resource.Name, out var resource))
            {
                writer.TryWrite(new TestRunEvent { Resource = ToState(resource, item.ResourceId, item.Snapshot) });
            }
        }
    }

    private IResource[] GetTestResources()
    {
        var resources = model.Resources.Where(r => r.TryGetLastAnnotation<TestAnnotation>(out _)).ToArray();
        if (resources.Length == 0)
        {
            throw new DistributedApplicationException("No test resources were found. Mark a resource with WithTestRun() before running aspire test.");
        }

        foreach (var resource in resources)
        {
            if (resource.GetReplicaCount() < 1)
            {
                throw new DistributedApplicationException($"Test resource '{resource.Name}' has no runnable replicas.");
            }
        }

        return resources;
    }

    private async Task<bool> WaitForCompletionAsync(IResource[] resources, Action<TestResourceState>? onCompleted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Observe every resource concurrently. A failed test must not stop other tests or their dependencies.
        var results = await Task.WhenAll(resources.Select(r => WaitForResourceAsync(r, onCompleted, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return results.All(result => result);
    }

    private async Task<bool> WaitForResourceAsync(IResource resource, Action<TestResourceState>? onCompleted, CancellationToken cancellationToken)
    {
        var completed = new HashSet<string>(StringComparers.ResourceName);
        var passed = true;

        while (completed.Count < resource.GetReplicaCount())
        {
            // WaitForResourceAsync atomically subscribes and replays current snapshots, including
            // tests that finished before the CLI connected. ResourceId distinguishes replicas.
            var terminal = await notifications.WaitForResourceAsync(resource.Name,
                e => !completed.Contains(e.ResourceId) && IsTerminal(e.Snapshot), cancellationToken).ConfigureAwait(false);
            var snapshot = terminal.Snapshot;

            if (snapshot.State?.Text == KnownResourceStates.FailedToStart ||
                snapshot.State?.Text == KnownResourceStates.RuntimeUnhealthy)
            {
                logger.LogError("Test resource '{ResourceName}' could not start ({State}).", terminal.ResourceId, snapshot.State.Text);
                passed = false;

                // A model-level startup failure occurs before DCP creates any replicas.
                if (terminal.ResourceId == resource.Name)
                {
                    onCompleted?.Invoke(ToState(resource, terminal.ResourceId, snapshot) with { Outcome = "failed" });
                    return false;
                }
            }
            else
            {
                if (snapshot.ExitCode is null)
                {
                    // DCP can emit Exited before its exit code. Give that same instance a bounded
                    // opportunity to publish the code; null is never evidence of a passing test.
                    using var timeout = new CancellationTokenSource(s_exitCodeTimeout, timeProvider);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                    try
                    {
                        terminal = await notifications.WaitForResourceAsync(resource.Name,
                            e => e.ResourceId == terminal.ResourceId && IsTerminal(e.Snapshot) && e.Snapshot.ExitCode is not null,
                            linked.Token).ConfigureAwait(false);
                        snapshot = terminal.Snapshot;
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError("Test resource '{ResourceName}' reached '{State}' but did not publish an exit code within {Timeout}.",
                            terminal.ResourceId, snapshot.State?.Text, s_exitCodeTimeout);
                    }
                }

                if (snapshot.ExitCode != 0)
                {
                    passed = false;
                    if (snapshot.ExitCode is not null)
                    {
                        logger.LogError("Test resource '{ResourceName}' failed with exit code {ExitCode}.", terminal.ResourceId, snapshot.ExitCode);
                    }
                }
            }

            completed.Add(terminal.ResourceId);
            var outcome = snapshot.State?.Text == KnownResourceStates.FailedToStart ||
                snapshot.State?.Text == KnownResourceStates.RuntimeUnhealthy || snapshot.ExitCode != 0
                ? "failed"
                : "passed";
            onCompleted?.Invoke(ToState(resource, terminal.ResourceId, snapshot) with { Outcome = outcome });
        }

        return passed;
    }

    private static TestResourceState ToState(IResource resource, string resourceId, CustomResourceSnapshot snapshot) => new()
    {
        ResourceName = resource.Name,
        // Nonterminal model snapshots are placeholders, not additional DCP replicas.
        ResourceId = resourceId == resource.Name && !IsTerminal(snapshot) ? null : resourceId,
        State = snapshot.State?.Text,
        ExitCode = snapshot.ExitCode,
        ExpectedReplicas = resource.GetReplicaCount()
    };

    private static bool IsTerminal(CustomResourceSnapshot snapshot) =>
        KnownResourceStates.TerminalStates.Contains(snapshot.State?.Text, StringComparers.ResourceState) ||
        snapshot.State?.Text == KnownResourceStates.RuntimeUnhealthy;
}
