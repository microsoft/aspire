// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aspire.Hosting.Cli;

#pragma warning disable ASPIRETESTING001 // Implements the experimental testing API.

/// <summary>
/// Observes test resources without changing the application's lifetime.
/// </summary>
internal sealed class TestRunCoordinator(
    DistributedApplicationModel model,
    ResourceNotificationService notifications,
    TimeProvider timeProvider,
    ILogger<TestRunCoordinator> logger) : IDisposable
{
    internal static readonly TimeSpan s_exitCodeTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly List<TestRunEvent> _events = [];
    private readonly List<Channel<TestRunEvent>> _subscribers = [];
    private CancellationTokenSource? _runCancellation;
    private Task<bool>? _run;
    private bool _ended;
    private Exception? _failure;

    public void Start(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_run is not null)
            {
                return;
            }
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _run = RunAsync(_runCancellation.Token);
            ObserveFaults(_run);
        }
    }

    public async Task<bool> WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Start(cancellationToken);
        return await _run!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TestRunEvent> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<TestRunEvent>();
        Start(cancellationToken);
        lock (_gate)
        {
            // Replay and subscribe under the same fence. Collectors can publish results before
            // the CLI connects, and none may disappear between replay and live observation.
            foreach (var item in _events)
            {
                channel.Writer.TryWrite(item);
            }
            if (_ended)
            {
                channel.Writer.TryComplete(_failure);
            }
            else
            {
                _subscribers.Add(channel);
            }
        }
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            var drain = false;
            lock (_gate)
            {
                _subscribers.Remove(channel);
                drain = !_ended && _subscribers.Count == 0;
            }
            if (drain)
            {
                _runCancellation!.Cancel();
                try
                {
                    await _run!.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
                {
                }
            }
        }
    }

    private void Publish(TestRunEvent item)
    {
        lock (_gate)
        {
            if (_ended)
            {
                throw new InvalidOperationException("The test run has already completed.");
            }
            _events.Add(item);
            foreach (var channel in _subscribers)
            {
                channel.Writer.TryWrite(item);
            }
        }
    }

    private async Task<bool> RunAsync(CancellationToken cancellationToken)
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
                Publish(new TestRunEvent
                {
                    Resource = new TestResourceState
                    {
                        ResourceName = resource.Name,
                        State = KnownResourceStates.NotStarted,
                        ExpectedReplicas = resource.GetReplicaCount()
                    }
                });
            }

            observations = ObserveAsync(resources, stop.Token);
            // The same authoritative task serves the run and collector completion helpers.
            // An early-returning collector cannot stop its resource or another test.
            var resourceCompletions = resources.ToDictionary(r => r.Name,
                r => WaitForResourceAsync(r, result => Publish(new TestRunEvent { Resource = result }), stop.Token),
                StringComparers.ResourceName);
            var collections = resources.Select(r => CollectAsync(r, resourceCompletions[r.Name], stop.Token)).ToArray();
            completion = CompleteAllAsync(resourceCompletions.Values.Concat(collections), stop.Token);
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
            Publish(new TestRunEvent { Passed = passed });
        }
        lock (_gate)
        {
            _failure = failure;
            _ended = true;
            foreach (var channel in _subscribers)
            {
                channel.Writer.TryComplete(failure);
            }
            _subscribers.Clear();
        }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return passed!.Value;
    }

    private async Task ObserveAsync(IResource[] resources, CancellationToken cancellationToken)
    {
        var names = resources.ToDictionary(r => r.Name, StringComparers.ResourceName);
        await foreach (var item in notifications.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            if (names.TryGetValue(item.Resource.Name, out var resource))
            {
                Publish(new TestRunEvent { Resource = ToState(resource, item.ResourceId, item.Snapshot) });
            }
        }
    }

    private IResource[] GetTestResources()
    {
        var resources = model.Resources.Where(r => r.TryGetLastAnnotation<TestingAnnotation>(out _)).ToArray();
        if (resources.Length == 0)
        {
            throw new DistributedApplicationException("No test resources were found. Mark a resource with WithTesting() before running aspire test.");
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

    private static async Task<bool> CompleteAllAsync(IEnumerable<Task<bool>> tasks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Observe every resource concurrently. A failed test must not stop other tests or their dependencies.
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return results.All(result => result);
    }

    private async Task<bool> CollectAsync(IResource resource, Task<bool> resourceCompletion, CancellationToken cancellationToken)
    {
        resource.TryGetLastAnnotation<TestingAnnotation>(out var annotation);
        var options = annotation?.Options;
        var collectors = options is { IsTestRun: true } ? options.Collectors : [];
        var state = new TestCollectionState
        {
            ResourceName = resource.Name,
            OutputPath = options?.OutputPath,
            CollectorCount = collectors.Length,
            Status = collectors.Length == 0 ? "notConfigured" : "running"
        };
        Publish(new TestRunEvent { Collection = state });
        if (collectors.Length == 0)
        {
            return true;
        }
        try
        {
            var outcomes = await Task.WhenAll(collectors.Select(collector =>
                RunCollectorAsync(resource.Name, options!, resourceCompletion, collector, cancellationToken))).ConfigureAwait(false);
            var errors = outcomes.Where(o => o.Error is not null).Select(o => o.Error).ToArray();
            Publish(new TestRunEvent
            {
                Collection = state with
                {
                    Status = errors.Length == 0 ? "completed" : "failed",
                    Error = errors.Length == 0 ? null : string.Join(Environment.NewLine, errors)
                }
            });
            return outcomes.All(o => o.Passed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(new TestRunEvent { Collection = state with { Status = "cancelled" } });
            throw;
        }
    }

    private async Task<(bool Passed, string? Error)> RunCollectorAsync(string resourceName, TestingOptions options,
        Task<bool> resourceCompletion, Func<ResourceTestingContext, Task> collector, CancellationToken cancellationToken)
    {
        var hasFailedResult = false;
        var context = new ResourceTestingContext(options.RunContext!, resourceName, options.OutputPath!, result =>
        {
            hasFailedResult |= result.Outcome == TestOutcome.Failed;
            Publish(new TestRunEvent
            {
                Result = new PublishedTestResult
                {
                    ResourceName = resourceName,
                    Name = result.Name,
                    Outcome = result.Outcome.ToString().ToLowerInvariant(),
                    DurationMilliseconds = result.Duration?.TotalMilliseconds,
                    Message = result.Message
                }
            });
        }, resourceCompletion, cancellationToken);
        // Avoid running a collector's synchronous prefix on the resource startup path.
        var task = Task.Run(() => collector(context), cancellationToken);
        string? error = null;
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Test result collection failed for resource '{ResourceName}'.", resourceName);
            error = $"Test result collection failed for resource '{resourceName}': {ex.Message}";
        }
        finally
        {
            // A non-cooperative callback may outlive cancellation. Close publication before
            // aggregate completion so its late results cannot change the finalized report.
            context.Close();
            ObserveFaults(task);
        }
        return (error is null && !hasFailedResult, error);
    }

    private static void ObserveFaults(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Dispose()
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
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
