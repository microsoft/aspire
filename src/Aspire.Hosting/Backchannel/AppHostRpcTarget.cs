// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Exec;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Backchannel;

internal class AppHostRpcTarget(
    ILogger<AppHostRpcTarget> logger,
    ResourceNotificationService resourceNotificationService,
    IServiceProvider serviceProvider,
    PipelineActivityReporter activityReporter,
    IHostApplicationLifetime lifetime,
    DistributedApplicationOptions options)
{
    private readonly CancellationTokenSource _shutdownCts = new();

    public async IAsyncEnumerable<BackchannelLogEntry> GetAppHostLogEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Create a linked token source that will be cancelled when shutdown is requested
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var linkedToken = linkedCts.Token;

        var loggerProvider = serviceProvider.GetService<BackchannelLoggerProvider>();
        if (loggerProvider is null)
        {
            yield break;
        }

        // Subscribe atomically: snapshot + channel for new entries, no gap
        var (snapshot, subscriberId, channel) = loggerProvider.Subscribe();

        try
        {
            // Replay buffered entries first so late-connecting clients see history
            foreach (var entry in snapshot)
            {
                yield return entry;
            }

            // Stream live entries
            await foreach (var entry in channel.Reader.ReadAllAsync(linkedToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
        finally
        {
            loggerProvider.Unsubscribe(subscriberId);
        }
    }

    public async IAsyncEnumerable<PublishingActivity> GetPublishingActivitiesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Create a linked token source that will be cancelled when shutdown is requested
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var linkedToken = linkedCts.Token;

        while (!linkedToken.IsCancellationRequested)
        {
            PublishingActivity? publishingActivity = null;
            
            try
            {
                publishingActivity = await activityReporter.ActivityItemUpdated.Reader.ReadAsync(linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdownCts.Token.IsCancellationRequested)
            {
                // Gracefully handle cancellation due to shutdown
                logger.LogDebug("Publishing activities stream cancelled due to AppHost shutdown");
                yield break;
            }

            // Terminate the stream if the publishing activity is null
            if (publishingActivity == null)
            {
                yield break;
            }

            yield return publishingActivity;
        }
    }

    public async IAsyncEnumerable<RpcResourceState> GetResourceStatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Create a linked token source that will be cancelled when shutdown is requested
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var linkedToken = linkedCts.Token;

        var resourceEvents = resourceNotificationService.WatchAsync(linkedToken);

        await foreach (var resourceEvent in resourceEvents.WithCancellation(linkedToken).ConfigureAwait(false))
        {
            if (resourceEvent.Resource.Name == "aspire-dashboard")
            {
                // Skip the dashboard resource, as it is handled separately.
                continue;
            }

            if (!resourceEvent.Resource.TryGetEndpoints(out var endpoints))
            {
                logger.LogTrace("Resource {ResourceName} does not have endpoints.", resourceEvent.Resource.Name);
                endpoints = Enumerable.Empty<EndpointAnnotation>();
            }

            var endpointUris = endpoints
                .Where(e => e.AllocatedEndpoint != null)
                .Select(e => e.AllocatedEndpoint!.UriString)
                .ToArray();

            // Compute health status
            var healthStatus = CustomResourceSnapshot.ComputeHealthStatus(resourceEvent.Snapshot.HealthReports, resourceEvent.Snapshot.State?.Text);

            yield return new RpcResourceState
            {
                Resource = resourceEvent.Resource.Name,
                Type = resourceEvent.Snapshot.ResourceType,
                State = resourceEvent.Snapshot.State?.Text ?? "Unknown",
                Endpoints = endpointUris,
                Health = healthStatus?.ToString()
            };
        }
    }

    public Task RequestStopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        
        // Cancel inflight streaming RPC calls before stopping the application
        _shutdownCts.Cancel();
        
        lifetime.StopApplication();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels inflight streaming RPC calls to allow graceful shutdown.
    /// This should be called before stopping the application to prevent JSON-RPC errors on clients.
    /// </summary>
    public void CancelInflightRpcCalls()
    {
        _shutdownCts.Cancel();
    }

    public async Task<DashboardUrlsState> GetDashboardUrlsAsync(CancellationToken cancellationToken)
    {
        if (!options.DashboardEnabled)
        {
            logger.LogError("Dashboard URL requested but dashboard is disabled.");
            throw new InvalidOperationException("Dashboard URL requested but dashboard is disabled.");
        }

        return await DashboardUrlsHelper.GetDashboardUrlsAsync(serviceProvider, logger, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<CommandOutput> ExecAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var execResourceManager = serviceProvider.GetRequiredService<ExecResourceManager>();
        var logsStream = execResourceManager.StreamExecResourceLogs(cancellationToken);
        await foreach (var commandOutput in logsStream.ConfigureAwait(false))
        {
            yield return commandOutput;
        }
    }

#pragma warning disable CA1822
    public Task<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        // The purpose of this API is to allow the CLI to determine what API surfaces
        // the AppHost supports. In 9.2 we'll be saying that you need a 9.2 apphost,
        // but the 9.3 CLI might actually support working with 9.2 apphosts. The idea
        // is that when the backchannel is established the CLI will call this API
        // and store the results. The "baseline.v0" capability is the bare minimum
        // that we need as of CLI version 9.2-preview*.
        //
        // Some capabilties will be opt in. For example in 9.3 we might refine the
        // publishing activities API to return more information, or add log streaming
        // features. So that would add a new capability that the apphsot can report
        // on initial backchannel negotiation and the CLI can adapt its behavior around
        // that. There may be scenarios where we need to break compataiblity at which
        // point we might increase the baseline version that the apphost reports.
        //
        // The ability to support a back channel at all is determined by the CLI by
        // making sure that the apphost version is at least > 9.2.

        _ = cancellationToken;
        return Task.FromResult(new string[] {
            "baseline.v2",
            "pipeline-steps.v1"
            });
    }
#pragma warning restore CA1822

    public async Task CompletePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken = default)
    {
        await activityReporter.CompleteInteractionAsync(promptId, answers, updateResponse: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken = default)
    {
        await activityReporter.CompleteInteractionAsync(promptId, answers, updateResponse: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GetPipelineStepsResponse> GetPipelineStepsAsync(GetPipelineStepsRequest? request = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Resolving pipeline steps for list-steps request.");

#pragma warning disable ASPIREPIPELINES001
        var pipeline = serviceProvider.GetRequiredService<IDistributedApplicationPipeline>() as DistributedApplicationPipeline
            ?? throw new InvalidOperationException("Pipeline is not a DistributedApplicationPipeline.");

        var model = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var executionContext = serviceProvider.GetRequiredService<DistributedApplicationExecutionContext>();

        var pipelineContext = new PipelineContext(model, executionContext, serviceProvider, logger, cancellationToken);

        var resolvedSteps = await pipeline.ResolveStepsAsync(pipelineContext).ConfigureAwait(false);

        // If a target step is specified, filter to its transitive dependencies
        if (!string.IsNullOrEmpty(request?.Step))
        {
            var stepsByName = resolvedSteps.ToDictionary(s => s.Name, StringComparer.Ordinal);
            if (stepsByName.TryGetValue(request.Step, out var targetStep))
            {
                resolvedSteps = DistributedApplicationPipeline.ComputeTransitiveDependencies(targetStep, stepsByName);
            }
            else
            {
                var availableSteps = string.Join(", ", resolvedSteps.Select(s => $"'{s.Name}'"));
                throw new InvalidOperationException(
                    $"Step '{request.Step}' not found in pipeline. Available steps: {availableSteps}");
            }
        }

        var orderedSteps = DistributedApplicationPipeline.GetTopologicalOrder(resolvedSteps);
#pragma warning restore ASPIREPIPELINES001

        return new GetPipelineStepsResponse
        {
            Steps = orderedSteps.Select(step => new PipelineStepInfo
            {
                Name = step.Name,
                Description = step.Description,
                DependsOn = [.. step.DependsOnSteps],
                Tags = [.. step.Tags],
                ResourceName = step.Resource?.Name
            }).ToArray()
        };
    }
}
