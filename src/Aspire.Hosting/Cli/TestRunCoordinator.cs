// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Cli;

/// <summary>
/// Observes resources marked with a <see cref="TestAnnotation"/> when the app host is launched via
/// <c>aspire test</c> (signalled by the <see cref="KnownConfigNames.CliTestMode"/> configuration value)
/// and streams <see cref="TestRunUpdate"/> values as each test resource starts and reaches a terminal
/// state.
/// </summary>
/// <remarks>
/// Unlike a normal <c>aspire run</c>, the lifetime of a test run is bounded by the tests themselves. The
/// CLI drives that lifetime: it consumes <see cref="WatchTestRunAsync(CancellationToken)"/> over the
/// backchannel and, once the stream completes (all test resources reached a terminal state), asks the app
/// host to stop. This service therefore only <em>observes</em> the model; it never calls
/// <c>StopApplication</c> itself. That keeps lifetime control in one place (the CLI) and means the orphan
/// detector still handles the "CLI disappeared" case.
/// </remarks>
internal sealed class TestRunCoordinator(
    IConfiguration configuration,
    DistributedApplicationModel model,
    ResourceNotificationService notificationService,
    ILogger<TestRunCoordinator> logger)
{
    /// <summary>
    /// Streams updates for every test resource until they have all reached a terminal state, at which
    /// point the enumerable completes. The CLI uses stream completion as the signal to stop the app host.
    /// </summary>
    public async IAsyncEnumerable<TestRunUpdate> WatchTestRunAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Only participate when the CLI explicitly requested test mode. Under a normal `aspire run` the
        // TestAnnotation has no effect on lifetime, so there is nothing to stream.
        if (configuration[KnownConfigNames.CliTestMode] is not { } value || !bool.TryParse(value, out var testMode) || !testMode)
        {
            yield break;
        }

        var testResources = model.Resources
            .Where(static r => r.TryGetAnnotationsOfType<TestAnnotation>(out _))
            .ToList();

        if (testResources.Count == 0)
        {
            // Nothing bounds the run. Completing the stream immediately lets the CLI treat this as a
            // warning and stop the app host rather than hang as if it were an interactive run.
            logger.LogWarning("Running in test mode but no resources are marked as test resources.");
            yield break;
        }

        logger.LogInformation(
            "Test mode active. Watching {Count} test resource(s): {Resources}.",
            testResources.Count,
            string.Join(", ", testResources.Select(static r => r.Name)));

        // Unbounded so the per-resource waiters never block when writing terminal results, and so the
        // initial "Running" burst can be queued before the CLI starts draining.
        var channel = Channel.CreateUnbounded<TestRunUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

        // Emit a Running update per resource up front so the CLI learns the full set (and total) before
        // any results arrive.
        foreach (var resource in testResources)
        {
            await channel.Writer.WriteAsync(
                new TestRunUpdate { Resource = resource.Name, Status = KnownTestRunStatuses.Running },
                cancellationToken).ConfigureAwait(false);
        }

        // Fan-in: one waiter per resource writes a terminal result, and the channel completes once they
        // have all finished. Run the join on a background task so we can yield results as they arrive.
        var waiters = testResources
            .Select(resource => WaitForTerminalAsync(resource, channel.Writer, cancellationToken))
            .ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(waiters).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, cancellationToken);

        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async Task WaitForTerminalAsync(IResource resource, ChannelWriter<TestRunUpdate> writer, CancellationToken cancellationToken)
    {
        // A test resource is "done" when it reaches any terminal state. FailedToStart counts as terminal
        // too, so a test that cannot even launch ends the run instead of hanging it.
        var resourceEvent = await notificationService.WaitForResourceAsync(
            resource.Name,
            static e => e.Snapshot.State?.Text is { } text && KnownResourceStates.TerminalStates.Contains(text),
            cancellationToken).ConfigureAwait(false);

        var (status, detail) = MapToStatus(resourceEvent.Snapshot);
        await writer.WriteAsync(
            new TestRunUpdate { Resource = resource.Name, Status = status, Detail = detail },
            cancellationToken).ConfigureAwait(false);
    }

    private static (string Status, string? Detail) MapToStatus(CustomResourceSnapshot snapshot)
    {
        var state = snapshot.State?.Text;

        // Finished == ran to completion successfully. Exited can be either; trust the exit code. Anything
        // else terminal (FailedToStart, or Exited with a non-zero code) is a failure.
        if (state == KnownResourceStates.Finished)
        {
            return (KnownTestRunStatuses.Passed, state);
        }

        if (state == KnownResourceStates.Exited)
        {
            return snapshot.ExitCode is 0 or null
                ? (KnownTestRunStatuses.Passed, $"{state} (exit code {snapshot.ExitCode ?? 0})")
                : (KnownTestRunStatuses.Failed, $"{state} (exit code {snapshot.ExitCode})");
        }

        return (KnownTestRunStatuses.Failed, state);
    }
}
