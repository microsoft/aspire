// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Backchannel;

internal class AppHostRpcTarget(
    ILogger<AppHostRpcTarget> logger,
    ResourceNotificationService resourceNotificationService,
    IServiceProvider serviceProvider,
    ProfilingTelemetry profilingTelemetry,
    PipelineActivityReporter activityReporter,
    IHostApplicationLifetime lifetime,
    DistributedApplicationOptions options,
    DistributedApplicationExecutionContext executionContext,
    AppHostStartupState startupState,
    IInteractionFileUploadStore fileUploadStore,
    IConfiguration configuration)
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Guid _watchGenerationId = Guid.NewGuid();

    public async IAsyncEnumerable<BackchannelLogEntry> GetAppHostLogEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Complete the stream immediately if shutdown has already been requested.
        if (_shutdownCts.IsCancellationRequested)
        {
            yield break;
        }

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

            // Stream live entries — uses a helper that swallows OperationCanceledException on cancellation
            // instead of propagating it, since yield return cannot appear in a try/catch block.
            await foreach (var entry in AsyncEnumerableUtils.ReadUntilCancelledAsync(channel.Reader.ReadAllAsync(linkedToken), linkedToken).ConfigureAwait(false))
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
        // Complete the stream immediately if shutdown has already been requested.
        if (_shutdownCts.IsCancellationRequested)
        {
            yield break;
        }

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
            catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
            {
                // Expected when the stream is cancelled due to shutdown or client disconnect.
                logger.LogDebug("Publishing activities stream cancelled.");
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
        // Complete the stream immediately if shutdown has already been requested.
        if (_shutdownCts.IsCancellationRequested)
        {
            yield break;
        }

        // Create a linked token source that will be cancelled when shutdown is requested
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var linkedToken = linkedCts.Token;

        var resourceEvents = resourceNotificationService.WatchAsync(linkedToken);

        // Use a helper that swallows OperationCanceledException on cancellation instead of propagating it,
        // since yield return cannot appear in a try/catch block.
        await foreach (var resourceEvent in AsyncEnumerableUtils.ReadUntilCancelledAsync(resourceEvents, linkedToken).ConfigureAwait(false))
        {
            if (string.Equals(resourceEvent.Resource.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName))
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

    public async IAsyncEnumerable<ResourceWatchStatus> WatchResourceWatchStatusAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_shutdownCts.IsCancellationRequested)
        {
            yield break;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var events = resourceNotificationService.WatchAsync(linkedCts.Token);
        var observedResources = new HashSet<IResource>(ReferenceEqualityComparer.Instance);
        yield return CreateResourceWatchStatus(observedResources);

        await foreach (var resourceEvent in AsyncEnumerableUtils.ReadUntilCancelledAsync(events, linkedCts.Token).ConfigureAwait(false))
        {
            observedResources.Add(resourceEvent.Resource);
            if (resourceEvent.Snapshot.Properties.Any(property => property.Name == ResourceWatch.StateProperty))
            {
                // Send an authoritative full snapshot, never a delta that could survive a departed generation.
                yield return CreateResourceWatchStatus(observedResources);
            }
        }
    }

#pragma warning disable ASPIREWATCH001 // Aggregate generic snapshot metadata, without a dependency on watcher mechanics.
    private ResourceWatchStatus CreateResourceWatchStatus(IEnumerable<IResource> observedResources)
    {
        var resources = new List<ResourceWatchResourceStatus>();
        // Notification replay discovers resources without enumerating a model that BeforeStart may still mutate
        // (for example, when the dashboard is disabled). Current-instance reads discard retired replica snapshots.
        foreach (var resource in observedResources)
        {
            // Watch observations are project/group-level. Replicas therefore contribute one logical resource,
            // with the most restrictive observed coverage until all of its current snapshots agree.
            var snapshots = resourceNotificationService.GetCurrentStates(resource).Select(item => item.Snapshot).ToArray();
            if (snapshots.Length == 0 && resource.Annotations.OfType<ResourceSnapshotAnnotation>().LastOrDefault() is { } initial)
            {
                snapshots = [initial.InitialSnapshot];
            }
            var coverage = snapshots
                .Select(snapshot => new
                {
                    State = snapshot.Properties.FirstOrDefault(property => property.Name == ResourceWatch.StateProperty)?.Value as string,
                    Reason = snapshot.Properties.FirstOrDefault(property => property.Name == ResourceWatch.ReasonProperty)?.Value as string,
                    LogResource = snapshot.Properties.FirstOrDefault(property => property.Name == ResourceWatch.LogResourceProperty)?.Value as string
                })
                .Where(item => item.State is not null)
                .OrderBy(item => item.State switch
                {
                    ResourceWatch.Failed => 1,
                    ResourceWatch.BuildFailed => 2,
                    ResourceWatch.Excluded => 3,
                    ResourceWatch.Pending => 4,
                    ResourceWatch.Watching => 5,
                    _ => 0
                })
                .FirstOrDefault();
            if (coverage is not null)
            {
                resources.Add(new()
                {
                    Resource = resource.Name,
                    State = coverage.State!,
                    Reason = coverage.Reason,
                    LogResource = coverage.LogResource
                });
            }
        }

        return new()
        {
            GenerationId = _watchGenerationId,
            DefaultEnabled = executionContext.RunConfiguration.WatchEnabled,
            Resources = [.. resources.OrderBy(resource => resource.Resource, StringComparer.Ordinal)]
        };
    }
#pragma warning restore ASPIREWATCH001

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
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(GetDashboardUrlsAsync), streaming: false);
        // The run command requests dashboard readiness, but publish mode never starts the
        // dashboard and relies on the publishing CLI to consume progress and stop the AppHost.
        if (executionContext.IsPublishMode)
        {
            throw new DistributedApplicationException(MessageStrings.PublishModeNotSupportedByRunCommand);
        }

        if (!options.DashboardEnabled)
        {
            logger.LogDebug("Dashboard URL requested but dashboard is disabled.");
            activity.SetDashboardHealthy(false);
            return new DashboardUrlsState { DashboardHealthy = false };
        }

        try
        {
            var urls = await DashboardUrlsHelper.GetDashboardUrlsAsync(serviceProvider, logger, cancellationToken).ConfigureAwait(false);
            activity.SetDashboardHealthy(urls.DashboardHealthy);
            return urls;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    public Task NotifyAppHostReadyAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        startupState.MarkReady();
        return Task.CompletedTask;
    }

#pragma warning disable CA1822
    public Task<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(GetCapabilitiesAsync), streaming: false);
        // The purpose of this API is to allow the CLI to determine what API surfaces
        // the AppHost supports. In 9.2 we'll be saying that you need a 9.2 apphost,
        // but the 9.3 CLI might actually support working with 9.2 apphosts. The idea
        // is that when the backchannel is established the CLI will call this API
        // and store the results. The "baseline.v0" capability is the bare minimum
        // that we need as of CLI version 9.2-preview*.
        //
        // Some capabilities will be opt in. For example in 9.3 we might refine the
        // publishing activities API to return more information, or add log streaming
        // features. So that would add a new capability that the apphost can report
        // on initial backchannel negotiation and the CLI can adapt its behavior around
        // that. There may be scenarios where we need to break compatibility at which
        // point we might increase the baseline version that the apphost reports.
        //
        // The ability to support a back channel at all is determined by the CLI by
        // making sure that the apphost version is at least > 9.2.

        _ = cancellationToken;
        return Task.FromResult(new string[] {
            "baseline.v2",
            "pipeline-steps.v1",
            "pipeline-steps.v2",
            ResourceWatch.Capability
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

    /// <summary>
    /// Registers a local file in the upload store by copying it to a managed temp location.
    /// Returns the file ID that can be used to reference the file in interaction responses.
    /// </summary>
    public async Task<UploadFileResponse> UploadFileAsync(UploadFileRequest request, CancellationToken cancellationToken = default)
    {
        var maxUploadSize = FileUploadHelpers.GetMaxFileUploadSize(configuration);

        if (request.Data.Length > maxUploadSize)
        {
            throw new InvalidOperationException($"File '{request.FileName}' exceeds the maximum upload size of {maxUploadSize} bytes.");
        }

        if (request.InteractionId <= 0)
        {
            throw new InvalidOperationException("An interaction ID is required when uploading a file.");
        }
        if (string.IsNullOrEmpty(request.InputName))
        {
            throw new InvalidOperationException("An input name is required when uploading a file.");
        }

        var (fileId, filePath) = fileUploadStore.CreateEntry(request.FileName, request.InteractionId, request.InputName);

        try
        {
            var destStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            await using (destStream.ConfigureAwait(false))
            {
                await destStream.WriteAsync(request.Data, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            fileUploadStore.RemoveEntry(request.InteractionId, fileId);
            throw;
        }

        fileUploadStore.CompleteUpload(request.InteractionId, fileId);

        return new UploadFileResponse { FileId = fileId };
    }

    public async Task<GetPipelineStepsResponse> GetPipelineStepsAsync(GetPipelineStepsRequest? request = null, CancellationToken cancellationToken = default)
    {
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(GetPipelineStepsAsync), streaming: false, request?.TraceContext);
        logger.LogDebug("Resolving pipeline steps for list-steps request.");

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
