// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
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
    BackchannelPipelineExecutionBarrier pipelineExecutionBarrier,
    IHostApplicationLifetime lifetime,
    DistributedApplicationOptions options,
    AppHostStartupState startupState,
    IInteractionFileUploadStore fileUploadStore,
    IConfiguration configuration)
{
    private readonly CancellationTokenSource _shutdownCts = new();

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

        pipelineExecutionBarrier.AllowExecution();

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
            "pipeline-resources.v1",
            "pipeline-inputs.v1"
            });
    }
#pragma warning restore CA1822

    public Task<GetPipelineResourcesResponse> GetPipelineResourcesAsync(GetPipelineResourcesRequest? request = null, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        logger.LogDebug("Resolving publish-mode resources for list-resources request.");

        var model = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var resources = model.Resources.ToArray();
        var snapshots = resources
            .Select(CreatePipelineResourceSnapshot)
            .Where(snapshot => request?.IncludeHidden == true || !IsHiddenResource(snapshot))
            .OrderBy(static snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(new GetPipelineResourcesResponse
        {
            Resources = snapshots
        });
    }

    public async Task<GetPipelineInputsResponse> GetPipelineInputsAsync(GetPipelineInputsRequest? request = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Resolving pipeline inputs for step '{StepName}'.", request?.Step);

        var model = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var executionContext = serviceProvider.GetRequiredService<DistributedApplicationExecutionContext>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var resolvedSteps = await ResolvePipelineStepsAsync(step: null, model, executionContext, cancellationToken).ConfigureAwait(false);
        var stepResources = PipelineParameterResolver.GetScopedResourcesForStep(request?.Step, resolvedSteps);
        var parameters = await PipelineParameterResolver.GetParameterResourcesAsync(model, executionContext, stepResources, cancellationToken).ConfigureAwait(false);

        return new GetPipelineInputsResponse
        {
            Inputs = [.. parameters.Select(parameter => CreatePipelineParameterInput(parameter, configuration))]
        };
    }

    public async Task ApplyPipelineInputValuesAsync(ApplyPipelineInputValuesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Values.Count == 0)
        {
            return;
        }

        var configurationManager = serviceProvider.GetService<IConfiguration>() as IConfigurationManager
            ?? throw new InvalidOperationException("Unable to apply pipeline input values because the AppHost configuration does not support mutation.");

        var model = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var executionContext = serviceProvider.GetRequiredService<DistributedApplicationExecutionContext>();
        var parameters = await PipelineParameterResolver.GetParameterResourcesAsync(model, executionContext, scopedResources: null, cancellationToken).ConfigureAwait(false);
        var parametersByName = parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parameterName, value) in request.Values)
        {
            if (!parametersByName.TryGetValue(parameterName, out var parameter))
            {
                throw new InvalidOperationException($"Parameter '{parameterName}' was not found in the application model.");
            }

            values[parameter.ConfigurationKey] = value;
        }

        configurationManager.AddInMemoryCollection(values);
        logger.LogDebug("Applied {InputCount} pipeline input value(s).", values.Count);
    }

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

        var model = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var executionContext = serviceProvider.GetRequiredService<DistributedApplicationExecutionContext>();
        var orderedSteps = await ResolvePipelineStepsAsync(request?.Step, model, executionContext, cancellationToken).ConfigureAwait(false);

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

    private static ResourceSnapshot CreatePipelineResourceSnapshot(IResource resource)
    {
        var snapshot = resource.Annotations.OfType<ResourceSnapshotAnnotation>().LastOrDefault()?.InitialSnapshot
            ?? new CustomResourceSnapshot
            {
                ResourceType = resource.GetResourceType(),
                Properties = [],
                Relationships = ApplicationModel.ResourceSnapshotBuilder.BuildRelationships(resource)
            };

        var relationships = ApplicationModel.ResourceSnapshotBuilder.BuildRelationships(resource)
            .Select(static relationship => new ResourceSnapshotRelationship
            {
                ResourceName = relationship.ResourceName,
                Type = relationship.Type
            })
            .ToArray();

        var urls = snapshot.Urls
            .Where(static url => !url.IsInactive && !string.IsNullOrEmpty(url.Url))
            .Select(static url => new ResourceSnapshotUrl
            {
                Name = url.Name ?? "default",
                Url = url.Url,
                IsInternal = url.IsInternal,
                DisplayProperties = new ResourceSnapshotUrlDisplayProperties
                {
                    DisplayName = string.IsNullOrEmpty(url.DisplayProperties.DisplayName) ? null : url.DisplayProperties.DisplayName,
                    SortOrder = url.DisplayProperties.SortOrder
                }
            })
            .ToArray();

        var healthReports = snapshot.HealthReports
            .Select(static healthReport => new ResourceSnapshotHealthReport
            {
                Name = healthReport.Name,
                Status = healthReport.Status?.ToString(),
                Description = healthReport.Description,
                ExceptionText = healthReport.ExceptionText
            })
            .ToArray();

        var volumes = snapshot.Volumes
            .Select(static volume => new ResourceSnapshotVolume
            {
                Source = volume.Source,
                Target = volume.Target,
                MountType = volume.MountType,
                IsReadOnly = volume.IsReadOnly
            })
            .ToArray();

        var environmentVariables = snapshot.EnvironmentVariables
            .Select(static environmentVariable => new ResourceSnapshotEnvironmentVariable
            {
                Name = environmentVariable.Name,
                Value = environmentVariable.Value,
                IsFromSpec = environmentVariable.IsFromSpec
            })
            .ToArray();

        var properties = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
        foreach (var property in snapshot.Properties)
        {
            properties[property.Name] = property.IsSensitive
                ? null
                : AuxiliaryBackchannelRpcTarget.ConvertPropertyValueToJsonNode(property.Value);
        }

        return new ResourceSnapshot
        {
            Name = resource.Name,
            DisplayName = resource.Name,
            ResourceType = snapshot.ResourceType,
            State = snapshot.State?.Text,
            StateStyle = snapshot.State?.Style,
            HealthStatus = snapshot.HealthStatus?.ToString(),
            ExitCode = snapshot.ExitCode,
            CreatedAt = snapshot.CreationTimeStamp,
            StartedAt = snapshot.StartTimeStamp,
            StoppedAt = snapshot.StopTimeStamp,
            Urls = urls,
            Relationships = relationships,
            HealthReports = healthReports,
            Volumes = volumes,
            EnvironmentVariables = environmentVariables,
            Properties = properties,
            IsHidden = snapshot.IsHidden || resource.IsExcludedFromPublish()
        };
    }

    private static bool IsHiddenResource(ResourceSnapshot snapshot) =>
        snapshot.IsHidden || string.Equals(snapshot.State, "Hidden", StringComparison.OrdinalIgnoreCase);

    private static PipelineInput CreatePipelineParameterInput(ParameterResource parameter, IConfiguration configuration)
    {
        var input = parameter.CreateInput();
        var value = configuration.GetValueWithNormalizedKey(parameter.ConfigurationKey);
        var required = string.IsNullOrEmpty(value) && parameter.Default is null;
        var hasConfiguredValue = !string.IsNullOrEmpty(value);

        return new PipelineInput
        {
            Name = parameter.Name,
            Kind = "parameter",
            ConfigurationKey = parameter.ConfigurationKey,
            Label = input.Label,
            Description = input.Description,
            EnableDescriptionMarkdown = input.EnableDescriptionMarkdown,
            InputType = input.InputType.ToString(),
            Required = required,
            Value = parameter.Secret ? null : value,
            HasValue = hasConfiguredValue || parameter.Default is not null,
            ValueSource = hasConfiguredValue ? "configuration" : parameter.Default is not null ? "default" : null,
            Options = input.Options?.ToDictionary(static option => option.Key, static option => (string?)option.Value, StringComparer.Ordinal),
            AllowCustomChoice = input.AllowCustomChoice,
            DynamicallyLoaded = input.DynamicLoading is not null,
            Disabled = input.Disabled,
            MaxLength = input.MaxLength
        };
    }

#pragma warning disable ASPIREPIPELINES001
    private async Task<IReadOnlyList<PipelineStep>> ResolvePipelineStepsAsync(
        string? step,
        DistributedApplicationModel model,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var pipeline = serviceProvider.GetRequiredService<IDistributedApplicationPipeline>() as DistributedApplicationPipeline
            ?? throw new InvalidOperationException("Pipeline is not a DistributedApplicationPipeline.");

        var pipelineContext = new PipelineContext(model, executionContext, serviceProvider, logger, cancellationToken);

        var resolvedSteps = await pipeline.ResolveStepsAsync(pipelineContext).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(step))
        {
            var stepsByName = resolvedSteps.ToDictionary(s => s.Name, StringComparer.Ordinal);
            if (stepsByName.TryGetValue(step, out var targetStep))
            {
                resolvedSteps = DistributedApplicationPipeline.ComputeTransitiveDependencies(targetStep, stepsByName);
            }
            else
            {
                var availableSteps = string.Join(", ", resolvedSteps.Select(s => $"'{s.Name}'"));
                throw new InvalidOperationException(
                    $"Step '{step}' not found in pipeline. Available steps: {availableSteps}");
            }
        }

        return DistributedApplicationPipeline.GetTopologicalOrder(resolvedSteps);
    }
#pragma warning restore ASPIREPIPELINES001
}
