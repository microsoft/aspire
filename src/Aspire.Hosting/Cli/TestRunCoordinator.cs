// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

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

        // Observe every resource concurrently. A failed test must not stop other tests or their dependencies.
        var results = await Task.WhenAll(resources.Select(r => WaitForResourceAsync(r, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return results.All(result => result);
    }

    private async Task<bool> WaitForResourceAsync(IResource resource, CancellationToken cancellationToken)
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
        }

        return passed;
    }

    private static bool IsTerminal(CustomResourceSnapshot snapshot) =>
        KnownResourceStates.TerminalStates.Contains(snapshot.State?.Text, StringComparers.ResourceState) ||
        snapshot.State?.Text == KnownResourceStates.RuntimeUnhealthy;
}
