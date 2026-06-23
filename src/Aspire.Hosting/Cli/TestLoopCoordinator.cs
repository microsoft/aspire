// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Cli;

/// <summary>
/// When the app host is launched via <c>aspire test</c> (signalled by the
/// <see cref="KnownConfigNames.CliTestMode"/> configuration value), this service waits for every
/// resource marked with a <see cref="TestAnnotation"/> to reach a terminal state and then shuts the
/// app host down. This is what bounds the lifetime of a test run to the tests themselves rather than
/// to an interactive Ctrl+C like <c>aspire run</c>.
/// </summary>
internal sealed class TestLoopCoordinator(
    IConfiguration configuration,
    DistributedApplicationModel model,
    ResourceNotificationService notificationService,
    IHostApplicationLifetime lifetime,
    ILogger<TestLoopCoordinator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only participate when the CLI explicitly requested test mode. Under a normal `aspire run`
        // the TestAnnotation has no effect on lifetime, so this service no-ops.
        if (configuration[KnownConfigNames.CliTestMode] is not { } value || !bool.TryParse(value, out var testMode) || !testMode)
        {
            return;
        }

        var testResources = model.Resources
            .Where(static r => r.TryGetAnnotationsOfType<TestAnnotation>(out _))
            .ToList();

        if (testResources.Count == 0)
        {
            // Nothing bounds the run, so there is nothing to wait for. Exit immediately rather than
            // hang as if it were an interactive run.
            logger.LogWarning("Running in test mode but no resources are marked as test resources. Shutting down.");
            lifetime.StopApplication();
            return;
        }

        logger.LogInformation(
            "Test mode active. Waiting for {Count} test resource(s) to complete: {Resources}.",
            testResources.Count,
            string.Join(", ", testResources.Select(static r => r.Name)));

        try
        {
            // A test resource is "done" when it reaches any terminal state. FailedToStart counts as
            // terminal too, so a test that cannot even launch ends the run instead of hanging it.
            await Task.WhenAll(testResources.Select(
                r => notificationService.WaitForResourceAsync(r.Name, KnownResourceStates.TerminalStates, stoppingToken)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app host is already shutting down (for example via Ctrl+C); nothing more to do.
            return;
        }

        logger.LogInformation("All test resources have completed. Shutting down the app host.");
        lifetime.StopApplication();
    }
}
