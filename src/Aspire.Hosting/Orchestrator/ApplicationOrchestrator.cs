// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001

using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Orchestrator;

internal sealed class ApplicationOrchestrator
{
    private readonly IDcpExecutor _dcpExecutor;
    private readonly DistributedApplicationModel _model;
    private readonly ILookup<IResource, IResource> _parentChildLookup;
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be supported until fully removed.
    private readonly IDistributedApplicationLifecycleHook[] _lifecycleHooks;
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be supported until fully removed.
    private readonly ResourceNotificationService _notificationService;
    private readonly ResourceLoggerService _loggerService;
    private readonly ILogger _logger;
    private readonly IDistributedApplicationEventing _eventing;
    private readonly IServiceProvider _serviceProvider;
    private readonly Uri? _dashboardUri;
    private readonly DistributedApplicationExecutionContext _executionContext;
    private readonly ParameterProcessor _parameterProcessor;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private IConfiguration? Configuration => _serviceProvider.GetService<IConfiguration>();

    public ApplicationOrchestrator(DistributedApplicationModel model,
                                   IDcpExecutor dcpExecutor,
                                   DcpExecutorEvents dcpExecutorEvents,
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be supported until fully removed.
                                   IEnumerable<IDistributedApplicationLifecycleHook> lifecycleHooks,
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be supported until fully removed.
                                   ResourceNotificationService notificationService,
                                   ResourceLoggerService loggerService,
                                   IDistributedApplicationEventing eventing,
                                   IServiceProvider serviceProvider,
                                   DistributedApplicationExecutionContext executionContext,
                                   ParameterProcessor parameterProcessor,
                                   IOptions<DashboardOptions> dashboardOptions,
                                   ILogger<ApplicationOrchestrator> logger)
    {
        _dcpExecutor = dcpExecutor;
        _model = model;
        _parentChildLookup = RelationshipEvaluator.GetParentChildLookup(model);
        _lifecycleHooks = lifecycleHooks.ToArray();
        _notificationService = notificationService;
        _loggerService = loggerService;
        _logger = logger;
        _eventing = eventing;
        _serviceProvider = serviceProvider;
        _executionContext = executionContext;
        _parameterProcessor = parameterProcessor;
        var dashboardUrl = dashboardOptions.Value.DashboardUrl?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        Uri.TryCreate(dashboardUrl, UriKind.Absolute, out _dashboardUri);

        dcpExecutorEvents.Subscribe<OnResourcesPreparedContext>(OnResourcesPrepared);
        dcpExecutorEvents.Subscribe<OnResourceChangedContext>(OnResourceChanged);
        dcpExecutorEvents.Subscribe<OnEndpointsAllocatedContext>(OnEndpointsAllocated);
        dcpExecutorEvents.Subscribe<OnResourceStartingContext>(OnResourceStarting);
        dcpExecutorEvents.Subscribe<OnConnectionStringAvailableContext>(OnConnectionStringAvailable);
        dcpExecutorEvents.Subscribe<OnResourceFailedToStartContext>(OnResourceFailedToStart);

        _eventing.Subscribe<ResourceEndpointsAllocatedEvent>(OnResourceEndpointsAllocated);
        _eventing.Subscribe<ConnectionStringAvailableEvent>(PublishConnectionStringValue);
        // Implement WaitFor functionality using BeforeResourceStartedEvent.
        _eventing.Subscribe<BeforeResourceStartedEvent>(WaitForInBeforeResourceStartedEvent);
    }

    private async Task PublishConnectionStringValue(ConnectionStringAvailableEvent @event, CancellationToken token)
    {
        if (@event.Resource is IResourceWithConnectionString resourceWithConnectionString)
        {
            var connectionString = await resourceWithConnectionString.GetConnectionStringAsync(token).ConfigureAwait(false);

            await _notificationService.PublishUpdateAsync(resourceWithConnectionString, state => state with
            {
                Properties = [.. state.Properties, new(CustomResourceKnownProperties.ConnectionString, connectionString) { IsSensitive = true }]
            })
            .ConfigureAwait(false);
        }
    }

    private async Task WaitForInBeforeResourceStartedEvent(BeforeResourceStartedEvent @event, CancellationToken cancellationToken)
    {
        using var activity = ProfilingTelemetry.StartResourceBeforeStartWait(Configuration, @event.Resource);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var waitForDependenciesTask = _notificationService.WaitForDependenciesAsync(@event.Resource, cts.Token);
        if (waitForDependenciesTask.IsCompletedSuccessfully)
        {
            // Nothing to wait for. Return immediately.
            return;
        }

        // Wait for either dependencies to be ready or for someone to move the resource out of a waiting state.
        // This happens when resource start command is run, which forces the status to "Starting".
        var waitForNonWaitingStateTask = _notificationService.WaitForResourceAsync(
            @event.Resource.Name,
            e => e.Snapshot.State?.Text != KnownResourceStates.Waiting,
            cts.Token);

        try
        {
            var completedTask = await Task.WhenAny(waitForDependenciesTask, waitForNonWaitingStateTask).ConfigureAwait(false);
            if (completedTask.IsFaulted)
            {
                // Make error visible from completed task.
                await completedTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
        finally
        {
            // Ensure both wait tasks are cancelled.
            cts.Cancel();
        }
    }

    private async Task OnEndpointsAllocated(OnEndpointsAllocatedContext context)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var afterEndpointsAllocatedEvent = new AfterEndpointsAllocatedEvent(_serviceProvider, _model);
#pragma warning restore CS0618 // Type or member is obsolete
        await _eventing.PublishAsync(afterEndpointsAllocatedEvent, context.CancellationToken).ConfigureAwait(false);

        foreach (var lifecycleHook in _lifecycleHooks)
        {
            await lifecycleHook.AfterEndpointsAllocatedAsync(_model, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishResourceEndpointUrls(IResource resource, CancellationToken cancellationToken)
    {
        // Process URLs for the resource.
        await ProcessResourceUrlCallbacks(resource, cancellationToken).ConfigureAwait(false);

        // Publish update with URLs.
        var urls = GetResourceUrls(resource);
        await _notificationService.PublishUpdateAsync(resource, s => s with { Urls = [.. urls] }).ConfigureAwait(false);
    }

    private static IEnumerable<UrlSnapshot> GetResourceUrls(IResource resource)
    {
        IEnumerable<UrlSnapshot> urls = [];
        if (resource.TryGetUrls(out var resourceUrls))
        {
            urls = resourceUrls.Select(url => new UrlSnapshot(Name: url.Endpoint?.EndpointName, Url: url.Url, IsInternal: url.DisplayLocation == UrlDisplayLocation.DetailsOnly)
            {
                // Endpoint URLs are inactive (hidden in the dashboard) when published here. It is assumed they will get activated later when the endpoint is considered active
                // by whatever allocated the endpoint in the first place, e.g. for resources controlled by DCP, when DCP detects the endpoint is listening.
                IsInactive = url.Endpoint is not null,
                DisplayProperties = new(url.DisplayText ?? "", url.DisplayOrder ?? 0)
            });
        }
        return urls;
    }

    private async Task OnResourceStarting(OnResourceStartingContext context)
    {
        switch (context.ResourceType)
        {
            case KnownResourceTypes.Project:
            case KnownResourceTypes.Executable:
                await PublishUpdateAsync(_notificationService, context.Resource, context.DcpResourceName, s => s with
                {
                    State = KnownResourceStates.Starting,
                    HealthReports = GetInitialHealthReports(context.Resource)
                })
                .ConfigureAwait(false);

                break;
            case KnownResourceTypes.Container:
                await PublishUpdateAsync(_notificationService, context.Resource, context.DcpResourceName, s => s with
                {
                    State = KnownResourceStates.Starting,
                    Properties = s.Properties.SetResourceProperty(KnownProperties.Container.Image, context.Resource.TryGetContainerImageName(out var imageName) ? imageName : ""),
                    HealthReports = GetInitialHealthReports(context.Resource)
                })
                .ConfigureAwait(false);

                Debug.Assert(context.DcpResourceName is not null, "Container that is starting should always include the DCP name.");
                await SetChildResourceAsync(context.Resource, state: KnownResourceStates.Starting, startTimeStamp: null, stopTimeStamp: null).ConfigureAwait(false);
                break;
            default:
                break;
        }

        var beforeResourceStartedEvent = new BeforeResourceStartedEvent(context.Resource, _serviceProvider);
        await _eventing.PublishAsync(beforeResourceStartedEvent, context.CancellationToken).ConfigureAwait(false);

        static Task PublishUpdateAsync(ResourceNotificationService notificationService, IResource resource, string? resourceId, Func<CustomResourceSnapshot, CustomResourceSnapshot> stateFactory)
        {
            return resourceId != null
                ? notificationService.PublishUpdateAsync(resource, resourceId, stateFactory)
                : notificationService.PublishUpdateAsync(resource, stateFactory);
        }
    }

    private async Task OnResourcesPrepared(OnResourcesPreparedContext context)
    {
        await PublishResourcesInitialStateAsync(context.CancellationToken).ConfigureAwait(false);
    }

    private async Task OnConnectionStringAvailable(OnConnectionStringAvailableContext context)
    {
        await PublishConnectionStringAvailableEvent(context.Resource, context.CancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessResourceUrlCallbacks(IResource resource, CancellationToken cancellationToken)
    {
        var urls = new List<ResourceUrlAnnotation>();
        EndpointAnnotation? primaryLaunchProfileEndpoint = null;

        try
        {
            // Project endpoints to URLs
            if (resource.TryGetEndpoints(out var endpoints) && resource is IResourceWithEndpoints resourceWithEndpoints)
            {
                foreach (var endpoint in endpoints)
                {
                    // Create a URL for each endpoint
                    Debug.Assert(endpoint.AllocatedEndpoint is not null, "Endpoint should be allocated at this point as we're calling this from ResourceEndpointsAllocatedEvent handler.");
                    if (endpoint.AllocatedEndpoint is not { } allocatedEndpoint)
                    {
                        continue;
                    }

                    if (endpoint.FromLaunchProfile && primaryLaunchProfileEndpoint is null)
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Setting primary launch profile endpoint to '{EndpointName}' for resource '{ResourceName}'.", endpoint.Name, resource.Name);
                        }
                        primaryLaunchProfileEndpoint = endpoint;
                    }

                    // The allocated endpoint is used for service discovery and is the primary URL displayed to
                    // the user. In general, if valid for a particular service binding, the allocated endpoint
                    // will be "localhost" as that's a valid address for the .NET developer certificate. However,
                    // if a service is bound to a specific IP address, the allocated endpoint will be that same IP
                    // address.
                    var endpointReference = new EndpointReference(resourceWithEndpoints, endpoint, endpoint.DefaultNetworkID);
                    var url = new ResourceUrlAnnotation { Url = allocatedEndpoint.UriString, Endpoint = endpointReference };

                    // In the case that a service is bound to multiple addresses or a *.localhost address, we generate
                    // additional URLs to indicate to the user other ways their service can be reached. If the service
                    // is bound to all interfaces (0.0.0.0, ::, etc.) we use the machine name as the additional
                    // address. If bound to a *.localhost address, we add the originally declared *.localhost address
                    // as an additional URL.
                    var additionalUrl = allocatedEndpoint.BindingMode switch
                    {
                        // The allocated address doesn't match the original target host, so include the target host as
                        // an additional URL.
                        EndpointBindingMode.SingleAddress when !allocatedEndpoint.Address.Equals(endpoint.TargetHost, StringComparison.OrdinalIgnoreCase) => new ResourceUrlAnnotation
                        {
                            Url = $"{allocatedEndpoint.UriScheme}://{endpoint.TargetHost}:{allocatedEndpoint.Port}",
                            Endpoint = endpointReference,
                        },
                        // For other single address bindings ("localhost", specific IP), don't include an additional URL.
                        EndpointBindingMode.SingleAddress => null,
                        // All other cases are binding to some set of all interfaces (IPv4, IPv6, or both), so add the machine
                        // name as an additional URL.
                        _ => new ResourceUrlAnnotation
                        {
                            Url = $"{allocatedEndpoint.UriScheme}://{Environment.MachineName}:{allocatedEndpoint.Port}",
                            Endpoint = endpointReference,
                        },
                    };

                    if (additionalUrl is not null && EndpointHostHelpers.IsLocalhostTld(additionalUrl.Endpoint?.EndpointAnnotation.TargetHost))
                    {
                        // If the additional URL is a *.localhost address we want to highlight that URL in the dashboard
                        additionalUrl.DisplayLocation = UrlDisplayLocation.SummaryAndDetails;
                        url.DisplayLocation = UrlDisplayLocation.DetailsOnly;

                        // Swap so that the *.localhost URL is the primary URL shown in the dashboard and targeted by `WithUrlForEndpoint` calls.
                        (additionalUrl, url) = (url, additionalUrl);
                    }
                    else if ((string.Equals(endpoint.UriScheme, "http", StringComparison.OrdinalIgnoreCase) || string.Equals(endpoint.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
                                && additionalUrl is null && EndpointHostHelpers.IsDevLocalhostTld(_dashboardUri))
                    {
                        // For HTTP endpoints, if the endpoint target host has not already resulted in an additional URL and the dashboard URL is using a *.dev.localhost address,
                        // we want to assign a *.dev.localhost address to every HTTP resource endpoint based on the dashboard URL.
                        // This allows users to access their services from the dashboard using a consistent pattern.
                        var subdomainSuffix = _dashboardUri.Host[.._dashboardUri.Host.IndexOf(".dev.localhost", StringComparison.OrdinalIgnoreCase)];

                        // Strip any "apphost" suffix that might be present on the dashboard name.
                        subdomainSuffix = TrimSuffix(subdomainSuffix, "apphost");

                        // Make the existing localhost URL the additional URL so it's not the primary endpoint URL shown in the dashboard or targeted by `WithUrlForEndpoint` calls.
                        additionalUrl = url;
                        additionalUrl.DisplayLocation = UrlDisplayLocation.DetailsOnly;

                        // Create the new primary URL using the *.dev.localhost pattern.
                        url = new ResourceUrlAnnotation
                        {
                            // <scheme>://<resource-name>-<subdomain-suffix>.dev.localhost:<port>
                            Url = $"{allocatedEndpoint.UriScheme}://{resource.Name.ToLowerInvariant()}-{subdomainSuffix}.dev.localhost:{allocatedEndpoint.Port}",
                            Endpoint = endpointReference,
                            DisplayLocation = UrlDisplayLocation.SummaryAndDetails
                        };

                        static string TrimSuffix(string value, string suffix)
                        {
                            char[] separators = ['-', '_', '.'];
                            Span<char> suffixSpan = stackalloc char[suffix.Length + 1];
                            foreach (var separator in separators)
                            {
                                suffixSpan[0] = separator;
                                suffix.CopyTo(suffixSpan[1..]);
                                if (value.EndsWith(suffixSpan, StringComparison.OrdinalIgnoreCase))
                                {
                                    return value[..^suffixSpan.Length];
                                }
                            }

                            return value;
                        }
                    }

                    urls.Add(url);
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Added URL '{Url}' for endpoint '{EndpointName}' on resource '{ResourceName}'.", url.Url, endpoint.Name, resource.Name);
                    }
                    if (additionalUrl is not null)
                    {
                        urls.Add(additionalUrl);
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Added additional URL '{Url}' for endpoint '{EndpointName}' on resource '{ResourceName}'.", additionalUrl.Url, endpoint.Name, resource.Name);
                        }
                    }
                }
            }

            // Add static URLs
            if (resource.TryGetUrls(out var staticUrls))
            {
                foreach (var staticUrl in staticUrls)
                {
                    urls.Add(staticUrl);
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Added static URL '{Url}' for resource '{ResourceName}'.", staticUrl.Url, resource.Name);
                    }

                    // Remove it from the resource here, we'll add it back later to avoid duplicates.
                    resource.Annotations.Remove(staticUrl);
                }
            }

            // Run the URL callbacks
            if (resource.TryGetAnnotationsOfType<ResourceUrlsCallbackAnnotation>(out var callbacks))
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Running {CallbackCount} URL callbacks for resource '{ResourceName}'.", callbacks.Count(), resource.Name);
                }
                var urlsCallbackContext = new ResourceUrlsCallbackContext(_executionContext, resource, urls, cancellationToken)
                {
                    Logger = _loggerService.GetLogger(resource)
                };
                var index = 0;
                foreach (var callback in callbacks)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Invoking URL callback '{CallbackIndex}' for resource '{ResourceName}'.", index, resource.Name);
                    }
                    await callback.Callback(urlsCallbackContext).ConfigureAwait(false);
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("{UrlCount} URLs after callback '{CallbackIndex}' for resource '{ResourceName}'.", urlsCallbackContext.Urls.Count, index, resource.Name);
                    }
                    index++;
                }
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("{UrlCount} URLs after calling '{CallbackIndex}' callback(s) for resource '{ResourceName}'.", urls.Count, index, resource.Name);
                }
            }

            // Apply path from primary launch profile endpoint URL to additional launch profile endpoint URLs.
            // This needs to happen after running URL callbacks as the application of the launch profile launchUrl happens in a callback.
            if (primaryLaunchProfileEndpoint is not null)
            {
                // Matches URL lookup logic in ProjectResourceBuilderExtensions.WithProjectDefaults
                var primaryUrl = urls.FirstOrDefault(u => string.Equals(u.Endpoint?.EndpointName, primaryLaunchProfileEndpoint.Name, StringComparisons.EndpointAnnotationName));
                if (primaryUrl is not null)
                {
                    Uri.TryCreate(primaryUrl.Url, UriKind.RelativeOrAbsolute, out var primaryUri);
                    var primaryPath = primaryUri?.IsAbsoluteUri == true ? primaryUri.AbsolutePath : primaryUrl.Url;

                    if (primaryPath.StartsWith('/') && primaryPath.Length > 1)
                    {
                        // The primary launch profile endpoint has a path, apply that path to all other non-relative launch profile endpoint URLs.
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Applying path '{Path}' from URL '{Url}' for primary launch profile endpoint '{EndpointName}' to other launch profile endpoints for resource '{ResourceName}'.", primaryPath, primaryUrl.Url, primaryLaunchProfileEndpoint.Name, resource.Name);
                        }
                        foreach (var url in urls)
                        {
                            if (url.Endpoint?.EndpointAnnotation == primaryLaunchProfileEndpoint
                                && !string.Equals(url.Url, primaryUrl.Url, StringComparisons.Url)
                                && Uri.IsWellFormedUriString(url.Url, UriKind.Absolute))
                            {
                                if (_logger.IsEnabled(LogLevel.Trace))
                                {
                                    _logger.LogTrace("Updating URL '{Url}' with path '{Path}' for launch profile endpoint '{EndpointName}' for resource '{ResourceName}'.", url.Url, primaryPath, url.Endpoint.EndpointName, resource.Name);
                                }
                                var uriBuilder = new UriBuilder(url.Url) { Path = primaryPath };
                                url.Url = uriBuilder.Uri.ToString();
                                if (_logger.IsEnabled(LogLevel.Trace))
                                {
                                    _logger.LogTrace("Updated URL to '{Url}' for launch profile endpoint '{EndpointName}' on resource '{ResourceName}'.", url.Url, url.Endpoint.EndpointName, resource.Name);
                                }
                            }
                        }
                    }
                    else
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("URL '{Url}' for primary launch profile endpoint '{EndpointName}' for resource '{ResourceName}' does not have a path to apply to other launch profile endpoints.", primaryUrl.Url, primaryLaunchProfileEndpoint.Name, resource.Name);
                        }
                    }
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace("Could not find URL for primary launch profile endpoint '{EndpointName}' for resource '{ResourceName}'.", primaryLaunchProfileEndpoint.Name, resource.Name);
                    }
                }
            }

            // Convert relative endpoint URLs to absolute URLs
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Converting relative endpoint URLs to absolute URLs for resource '{ResourceName}'.", resource.Name);
            }
            foreach (var url in urls)
            {
                if (url.Endpoint is { } endpoint)
                {
                    if (url.Url.StartsWith('/') && endpoint.AllocatedEndpoint is { } allocatedEndpoint)
                    {
                        url.Url = allocatedEndpoint.UriString.TrimEnd('/') + url.Url;
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Converted relative URL to absolute URL '{Url}' for endpoint '{EndpointName}' on resource '{ResourceName}'.", url.Url, endpoint.EndpointName, resource.Name);
                        }
                    }
                }
            }

            // Add URLs
            var count = 0;
            foreach (var url in urls)
            {
                resource.Annotations.Add(url);
                count++;
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Added URL annotation '{Url}' to resource '{ResourceName}'.", url.Url, resource.Name);
                }
            }
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Added total of {UrlCount} URLs to resource '{ResourceName}'", count, resource.Name);
            }
        }
        catch (Exception ex)
        {
            var resourceLogger = _loggerService.GetLogger(resource);
            resourceLogger.LogError(ex, "An error occurred while processing URLs for resource '{ResourceName}': {Message}", resource.Name, ex.Message);
        }
    }

    private async Task OnResourceEndpointsAllocated(ResourceEndpointsAllocatedEvent @event, CancellationToken cancellationToken)
    {
        await PublishResourceEndpointUrls(@event.Resource, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnResourceChanged(OnResourceChangedContext context)
    {
        // Get the previous state before updating to detect transitions to stopped states
        string? previousState = null;
        if (_notificationService.TryGetCurrentState(context.DcpResourceName, out var previousResourceEvent))
        {
            previousState = previousResourceEvent.Snapshot.State?.Text;
        }

        await _notificationService.PublishUpdateAsync(context.Resource, context.DcpResourceName, context.UpdateSnapshot).ConfigureAwait(false);

        if (context.ResourceType == KnownResourceTypes.Container)
        {
            await SetChildResourceAsync(context.Resource, context.Status.State, context.Status.StartupTimestamp, context.Status.FinishedTimestamp).ConfigureAwait(false);
        }

        // Check if the resource has transitioned to a terminal/stopped state
        var currentState = context.Status.State;
        if (currentState is not null &&
            KnownResourceStates.TerminalStates.Contains(currentState) &&
            previousState != currentState &&
            (previousState is null ||
            !KnownResourceStates.TerminalStates.Contains(previousState)))
        {
            // Get the current state from notification service after the update
            if (_notificationService.TryGetCurrentState(context.DcpResourceName, out var currentResourceEvent))
            {
                // Resource has transitioned from a non-terminal state to a terminal state - fire ResourceStoppedEvent
                await PublishEventToHierarchy(r => new ResourceStoppedEvent(r, _serviceProvider, currentResourceEvent), context.Resource, context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task OnResourceFailedToStart(OnResourceFailedToStartContext context)
    {
        if (context.DcpResourceName != null)
        {
            await _notificationService.PublishUpdateAsync(context.Resource, context.DcpResourceName, s => s with { State = KnownResourceStates.FailedToStart }).ConfigureAwait(false);

            if (context.ResourceType == KnownResourceTypes.Container)
            {
                await SetChildResourceAsync(context.Resource, KnownResourceStates.FailedToStart, startTimeStamp: null, stopTimeStamp: null).ConfigureAwait(false);
            }
        }
        else
        {
            await _notificationService.PublishUpdateAsync(context.Resource, s => s with { State = KnownResourceStates.FailedToStart }).ConfigureAwait(false);
        }
    }

    public async Task RunApplicationAsync(CancellationToken cancellationToken = default)
    {
        await _dcpExecutor.RunApplicationAsync(cancellationToken).ConfigureAwait(false);

        var afterResourcesCreatedEvent = new AfterResourcesCreatedEvent(_serviceProvider, _model);
        await _eventing.PublishAsync(afterResourcesCreatedEvent, cancellationToken).ConfigureAwait(false);

        foreach (var lifecycleHook in _lifecycleHooks)
        {
            await lifecycleHook.AfterResourcesCreatedAsync(_model, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCancellation.Cancel();

        await _dcpExecutor.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartResourceAsync(string resourceName, CancellationToken cancellationToken)
    {
        var resourceReference = _dcpExecutor.GetResource(resourceName);

        // Figure out if the resource is waiting or not using PublishUpdateAsync, and if it is then set the
        // state to "Starting" to force waiting to complete.
        var isWaiting = false;
        await _notificationService.PublishUpdateAsync(
            resourceReference.ModelResource,
            resourceReference.DcpResourceName,
            s =>
            {
                if (s.State?.Text == KnownResourceStates.Waiting)
                {
                    isWaiting = true;
                    return s with { State = KnownResourceStates.Starting };
                }

                return s;
            }).ConfigureAwait(false);

        // A waiting resource is already trying to start up and asking DCP to start it will result in a conflict.
        // We only want to ask the DCP to start the resource if it wasn't.
        if (!isWaiting)
        {
            await _dcpExecutor.StartResourceAsync(resourceReference, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopResourceAsync(string resourceName, CancellationToken cancellationToken)
    {
        var resourceReference = _dcpExecutor.GetResource(resourceName);
        await _dcpExecutor.StopResourceAsync(resourceReference, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetChildResourceAsync(IResource resource, string? state, DateTime? startTimeStamp, DateTime? stopTimeStamp)
    {
        foreach (var child in _parentChildLookup[resource].Where(c => c is IResourceWithParent))
        {
            // Don't propagate state to resources that have a life of their own.
            if (ResourceHasOwnLifetime(child))
            {
                continue;
            }

            await _notificationService.PublishUpdateAsync(child, s => s with
            {
                State = state,
                StartTimeStamp = startTimeStamp,
                StopTimeStamp = stopTimeStamp
            }).ConfigureAwait(false);

            // Recurse to set the child resources of the child.
            await SetChildResourceAsync(child, state, startTimeStamp, stopTimeStamp)
                .ConfigureAwait(false);
        }
    }

    private async Task PublishResourcesInitialStateAsync(CancellationToken cancellationToken)
    {
        // Initialize all parameter resources up front
        await _parameterProcessor.InitializeParametersAsync(_model.Resources.OfType<ParameterResource>(), waitForResolution: false).ConfigureAwait(false);

        // Publish the initial state of the resources that have a snapshot annotation.
        foreach (var resource in _model.Resources)
        {
            // Process relationships for the resource.
            var relationships = ApplicationModel.ResourceSnapshotBuilder.BuildRelationships(resource);
            var parent = resource is IResourceWithParent hasParent
                ? hasParent.Parent
                : resource.Annotations.OfType<ResourceRelationshipAnnotation>().LastOrDefault(r => r.Type == KnownRelationshipTypes.Parent)?.Resource;
            var urls = GetResourceUrls(resource);

            await _notificationService.PublishUpdateAsync(resource, s =>
            {
                return s with
                {
                    Relationships = relationships,
                    Urls = [.. urls],
                    Properties = parent is null ? s.Properties : s.Properties.SetResourceProperty(KnownProperties.Resource.ParentName, parent.GetResolvedResourceNames()[0]),
                    HealthReports = GetInitialHealthReports(resource)
                };
            }).ConfigureAwait(false);

            // Notify resources that they need to initialize themselves.
            var initializeEvent = new InitializeResourceEvent(resource, _eventing, _loggerService, _notificationService, _serviceProvider);
            await _eventing.PublishAsync(initializeEvent, EventDispatchBehavior.NonBlockingConcurrent, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ImmutableArray<HealthReportSnapshot> GetInitialHealthReports(IResource resource)
    {
        if (!resource.TryGetAnnotationsIncludingAncestorsOfType<HealthCheckAnnotation>(out var annotations))
        {
            return [];
        }

        var reports = annotations.Select(annotation => new HealthReportSnapshot(annotation.Key, null, null, null));
        return [.. reports];
    }

    private async Task PublishConnectionStringAvailableEvent(IResource resource, CancellationToken cancellationToken)
    {
        // If the resource itself has a connection string then publish that the connection string is available.
        if (resource is IResourceWithConnectionString)
        {
            var connectionStringAvailableEvent = new ConnectionStringAvailableEvent(resource, _serviceProvider);
            await _eventing.PublishAsync(connectionStringAvailableEvent, cancellationToken).ConfigureAwait(false);
        }

        // Sometimes the container/executable itself does not have a connection string, and in those cases
        // we need to dispatch the event for the children.
        if (_parentChildLookup[resource] is { } children)
        {
            // only dispatch the event for children that have a connection string and are IResourceWithParent, not parented by annotations.
            foreach (var child in children.OfType<IResourceWithConnectionString>().Where(c => c is IResourceWithParent))
            {
                if (ResourceHasOwnLifetime(child))
                {
                    continue;
                }

                await PublishConnectionStringAvailableEvent(child, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PublishEventToHierarchy<TEvent>(Func<IResource, TEvent> createEvent, IResource resource, CancellationToken cancellationToken)
        where TEvent : IDistributedApplicationResourceEvent
    {
        // Publish the event to the resource itself.
        await _eventing.PublishAsync(createEvent(resource), cancellationToken).ConfigureAwait(false);

        // Publish the event to all child resources.
        if (_parentChildLookup[resource] is { } children)
        {
            foreach (var child in children.Where(c => c is IResourceWithParent))
            {
                if (ResourceHasOwnLifetime(child))
                {
                    continue;
                }

                await PublishEventToHierarchy(createEvent, child, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // TODO: We need to introduce a formal way to resources to opt into propagating state and events to children.
    // This fixes the immediate problem of not propagating to top-level resources, but there are other
    // resources that may want to have their own lifetime, that this code will be unaware of.
    private static bool ResourceHasOwnLifetime(IResource resource) =>
        resource.IsContainer() ||
        resource is ProjectResource ||
        resource is ExecutableResource ||
        resource is ParameterResource ||
        resource is ConnectionStringResource ||
        resource is ExternalServiceResource;
}
