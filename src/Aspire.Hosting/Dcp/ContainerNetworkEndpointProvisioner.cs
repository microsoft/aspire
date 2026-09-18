// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// A host resource with endpoints that require a representation on the default container network.
/// </summary>
internal readonly record struct HostResourceWithEndpoints(
    IResourceWithEndpoints Resource,
    IEnumerable<EndpointAnnotation> Endpoints)
{
    internal static HostResourceWithEndpoints? Create(IResource resource)
    {
        if (resource is IResourceWithEndpoints resourceWithEndpoints && !resource.IsContainer())
        {
            var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToArray();
            if (endpoints.Length > 0)
            {
                return new HostResourceWithEndpoints(resourceWithEndpoints, endpoints);
            }
        }

        return null;
    }
}

/// <summary>
/// A DCP service that represents a host resource endpoint on the default container network.
/// </summary>
internal sealed class ContainerNetworkService
{
    public required AppResource<Service> ServiceResource { get; init; }
    public TunnelConfiguration? TunnelConfig { get; init; }
}

/// <summary>
/// Provisions endpoint representations that make host resources reachable from the default container network.
/// </summary>
internal sealed partial class ContainerNetworkEndpointProvisioner : IDisposable
{
    private const string DefaultAspireNetworkName = "aspire-session-network";
    private const string DefaultAspirePersistentNetworkName = "aspire-persistent-network";
    private const string ContainerTunnelContainerName = "aspire";

    private readonly IConfiguration _configuration;
    private readonly IOptions<DcpOptions> _options;
    private readonly DcpNameGenerator _nameGenerator;
    private readonly DistributedApplicationModel _model;
    private readonly ResourceLoggerService _loggerService;
    private readonly IDcpDependencyCheckService _dcpDependencyCheckService;
    private readonly ILogger<ContainerNetworkEndpointProvisioner> _logger;
    private readonly string _normalizedApplicationName;
    private readonly DcpAppResourceStore _appResources;
    private readonly SemaphoreSlim _tunnelSemaphore = new(1, 1);
    private readonly List<TunnelConfiguration> _tunnelConfigurations = [];
    private readonly Dictionary<EndpointAnnotation, TaskCompletionSource> _endpointProvisioning = new(ReferenceEqualityComparer.Instance);
    private Task<AppResource<ContainerNetworkTunnelProxy>>? _tunnelCreationTask;

    public ContainerNetworkEndpointProvisioner(
        IConfiguration configuration,
        IOptions<DcpOptions> options,
        DcpNameGenerator nameGenerator,
        DistributedApplicationModel model,
        ResourceLoggerService loggerService,
        IDcpDependencyCheckService dcpDependencyCheckService,
        IHostEnvironment hostEnvironment,
        ILogger<ContainerNetworkEndpointProvisioner> logger,
        DcpAppResourceStore appResources)
    {
        _configuration = configuration;
        _options = options;
        _nameGenerator = nameGenerator;
        _model = model;
        _loggerService = loggerService;
        _dcpDependencyCheckService = dcpDependencyCheckService;
        _logger = logger;
        _normalizedApplicationName = NormalizeApplicationName(hostEnvironment.ApplicationName);
        _appResources = appResources;
    }

    public void Dispose()
    {
        _tunnelSemaphore.Dispose();
    }

    internal bool CanProvisionEndpoint(EndpointAnnotation endpoint)
    {
        return !_options.Value.EnableAspireContainerTunnel || endpoint.Protocol == ProtocolType.Tcp;
    }

    internal void PrepareContainerNetwork()
    {
        var containerResources = _model.Resources.Where(resource => resource.IsContainer());
        if (!containerResources.Any())
        {
            return;
        }

        var network = ContainerNetwork.Create(KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value);
        if (containerResources.Any(resource => resource.GetLifetimeType() == Lifetime.Persistent))
        {
            network.Spec.Persistent = true;
            network.Spec.NetworkName = $"{DefaultAspirePersistentNetworkName}-{_nameGenerator.GetProjectHashSuffix()}";
        }
        else
        {
            network.Spec.NetworkName = $"{DefaultAspireNetworkName}-{DcpNameGenerator.GetRandomNameSuffix()}";
        }

        if (!string.IsNullOrEmpty(_normalizedApplicationName))
        {
            var shortApplicationName = _normalizedApplicationName.Length < 32
                ? _normalizedApplicationName
                : _normalizedApplicationName[..32];
            network.Spec.NetworkName += $"-{shortApplicationName}";
        }

        _appResources.Add(new AppResource<ContainerNetwork>(network));
    }

    internal void ValidateContainerNameConflicts(IEnumerable<IResource> containerResources)
    {
        if (!_options.Value.EnableAspireContainerTunnel)
        {
            return;
        }

        foreach (var container in containerResources)
        {
            if (IsContainerTunnelContainerName(container.Name))
            {
                throw new DistributedApplicationException($"Container resource name '{container.Name}' conflicts with the Aspire container tunnel container name '{ContainerTunnelContainerName}'. Rename the resource or disable the Aspire container tunnel.");
            }

            if (container.TryGetLastAnnotation<ContainerNameAnnotation>(out var containerNameAnnotation) &&
                IsContainerTunnelContainerName(containerNameAnnotation.Name))
            {
                throw new DistributedApplicationException($"Container resource '{container.Name}' uses container name '{containerNameAnnotation.Name}', which conflicts with the Aspire container tunnel container name '{ContainerTunnelContainerName}'. Rename the container or disable the Aspire container tunnel.");
            }

            foreach (var aliasAnnotation in container.Annotations.OfType<ContainerNetworkAliasAnnotation>())
            {
                if (IsContainerTunnelContainerName(aliasAnnotation.Alias))
                {
                    throw new DistributedApplicationException($"Container resource '{container.Name}' uses network alias '{aliasAnnotation.Alias}', which conflicts with the Aspire container tunnel container name '{ContainerTunnelContainerName}'. Rename the alias or disable the Aspire container tunnel.");
                }
            }
        }

        static bool IsContainerTunnelContainerName(string name)
            => string.Equals(name, ContainerTunnelContainerName, StringComparison.OrdinalIgnoreCase);
    }

    internal IEnumerable<ContainerNetworkService> CreateContainerNetworkServices(HostResourceWithEndpoints hostResource)
    {
        var services = new List<ContainerNetworkService>();
        var useTunnel = _options.Value.EnableAspireContainerTunnel;
        var tunnelProxyName = useTunnel ? GetTunnelProxyResourceName() : "";

        foreach (var endpoint in hostResource.Endpoints)
        {
            var (serviceName, isNew) = _nameGenerator.GetServiceName(
                hostResource.Resource,
                endpoint,
                KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
            if (!isNew)
            {
                continue;
            }

            var service = Service.Create(serviceName);
            service.Spec.AddressAllocationMode = AddressAllocationModes.Proxyless;
            service.Spec.Protocol = PortProtocol.FromProtocolType(endpoint.Protocol);

            var serverService = _appResources.Get().OfType<ServiceWithModelResource>().FirstOrDefault(serviceWithResource =>
                StringComparers.ResourceName.Equals(serviceWithResource.ModelResource.Name, hostResource.Resource.Name) &&
                StringComparers.EndpointAnnotationName.Equals(serviceWithResource.EndpointAnnotation.Name, endpoint.Name));
            if (serverService is null)
            {
                throw new InvalidDataException($"Host endpoint '{endpoint.Name}' on resource '{hostResource.Resource.Name}' should have an associated DCP Service resource already set up");
            }

            TunnelConfiguration? tunnelConfiguration = null;
            if (useTunnel)
            {
                tunnelConfiguration = new TunnelConfiguration
                {
                    Name = serviceName,
                    ServerServiceName = serverService.DcpResource.Metadata.Name,
                    ServerServiceNamespace = string.Empty,
                    ClientServiceName = service.Metadata.Name,
                    ClientServiceNamespace = string.Empty
                };
            }

            service.Annotate(CustomResource.ResourceNameAnnotation, hostResource.Resource.Name);
            service.Annotate(CustomResource.EndpointNameAnnotation, endpoint.Name);
            service.Annotate(CustomResource.ContainerNetworkAnnotation, KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value);
            service.Annotate(CustomResource.PrimaryServiceNameAnnotation, serverService.DcpResource.Metadata.Name);
            service.Annotate(CustomResource.ContainerTunnelInstanceName, tunnelProxyName);

            services.Add(new ContainerNetworkService
            {
                ServiceResource = new AppResource<Service>(service),
                TunnelConfig = tunnelConfiguration
            });
        }

        return services;
    }

    internal async Task EnsureEndpointsAsync(
        ImmutableArray<HostResourceWithEndpoints> hostResources,
        ContainerNetworkEndpointContext context,
        IDcpObjectFactory factory,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.EnableAspireContainerTunnel)
        {
            await context.ContainerNetworkReady.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var provisioningTasks = new List<Task>();
        var newEndpointsByResource = new Dictionary<IResourceWithEndpoints, List<EndpointAnnotation>>(ReferenceEqualityComparer.Instance);
        var newEndpointCompletions = new List<TaskCompletionSource>();

        // Serialize discovery so concurrent consumers can provision shared endpoints in one tunnel update.
        await _tunnelSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var hostResource in hostResources)
            {
                foreach (var endpoint in hostResource.Endpoints)
                {
                    if (!CanProvisionEndpoint(endpoint))
                    {
                        _loggerService.GetLogger(hostResource.Resource).LogWarning(
                            "Host endpoint '{EndpointName}' on resource '{HostResource}' is referenced from the default Aspire container network, but the endpoint uses the unsupported network protocol '{Protocol}'. The Aspire container tunnel supports only TCP.",
                            endpoint.Name,
                            hostResource.Resource.Name,
                            endpoint.Protocol);
                        continue;
                    }

                    if (!_endpointProvisioning.TryGetValue(endpoint, out var completion))
                    {
                        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _endpointProvisioning.Add(endpoint, completion);
                        newEndpointCompletions.Add(completion);

                        if (!newEndpointsByResource.TryGetValue(hostResource.Resource, out var endpoints))
                        {
                            endpoints = [];
                            newEndpointsByResource.Add(hostResource.Resource, endpoints);
                        }

                        endpoints.Add(endpoint);
                    }

                    provisioningTasks.Add(completion.Task);
                }
            }
        }
        finally
        {
            _tunnelSemaphore.Release();
        }

        if (newEndpointsByResource.Count > 0)
        {
            var newHostResources = newEndpointsByResource
                .Select(pair => new HostResourceWithEndpoints(pair.Key, pair.Value))
                .ToImmutableArray();

            // Endpoint provisioning belongs to the application run, not to whichever resource first requested it.
            // Callers can cancel their own waits without cancelling or poisoning the shared provisioning operation.
            _ = CompleteEndpointProvisioningAsync(
                newHostResources,
                newEndpointCompletions,
                context,
                factory);
        }

        await Task.WhenAll(provisioningTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteEndpointProvisioningAsync(
        ImmutableArray<HostResourceWithEndpoints> hostResources,
        IReadOnlyList<TaskCompletionSource> endpointCompletions,
        ContainerNetworkEndpointContext context,
        IDcpObjectFactory factory)
    {
        try
        {
            await ProvisionEndpointsAsync(
                hostResources,
                context,
                factory,
                context.ApplicationRunCancellationToken).ConfigureAwait(false);

            foreach (var completion in endpointCompletions)
            {
                completion.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (context.ApplicationRunCancellationToken.IsCancellationRequested)
        {
            foreach (var completion in endpointCompletions)
            {
                completion.TrySetCanceled(context.ApplicationRunCancellationToken);
            }
        }
        catch (Exception ex)
        {
            foreach (var completion in endpointCompletions)
            {
                completion.TrySetException(ex);
            }
        }
    }

    private async Task ProvisionEndpointsAsync(
        ImmutableArray<HostResourceWithEndpoints> hostResources,
        ContainerNetworkEndpointContext context,
        IDcpObjectFactory factory,
        CancellationToken cancellationToken)
    {
        var containerNetworkServices = hostResources.SelectMany(CreateContainerNetworkServices).ToArray();
        if (containerNetworkServices.Length != hostResources.Sum(resource => resource.Endpoints.Count()))
        {
            throw new InvalidOperationException("Failed to create a container-network service for every requested host endpoint.");
        }

        await Task.WhenAll([context.ContainerNetworkReady, context.WorkloadEndpointsReady])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var serviceObjects = containerNetworkServices.Select(service => service.ServiceResource.DcpResource).ToArray();
        await factory.CreateDcpObjectsAsync(serviceObjects, cancellationToken).ConfigureAwait(false);

        var newTunnels = containerNetworkServices
            .Where(service => service.TunnelConfig is not null)
            .Select(service => service.TunnelConfig!)
            .ToArray();
        Debug.Assert(newTunnels.Length == containerNetworkServices.Length, "Each tunneled service should have a tunnel config.");
        var tunnelConfigurationIsCurrent = false;

        await _tunnelSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _tunnelConfigurations.AddRange(newTunnels);
            if (_tunnelCreationTask is null)
            {
                _tunnelCreationTask = CreateTunnelProxyResourceAsync(
                    factory,
                    _tunnelConfigurations.ToList(),
                    context.ApplicationRunCancellationToken);
                tunnelConfigurationIsCurrent = true;
            }
        }
        finally
        {
            _tunnelSemaphore.Release();
        }

        var tunnelProxyResource = await _tunnelCreationTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (!tunnelConfigurationIsCurrent)
        {
            // Each patch must include the complete accumulated tunnel configuration, so serialize updates.
            await _tunnelSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await factory.PatchDcpObjectAsync(
                    tunnelProxyResource.DcpResource,
                    proxy => proxy.Spec.Tunnels = _tunnelConfigurations.ToList(),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _tunnelSemaphore.Release();
            }
        }

        await factory.UpdateWithEffectiveAddressInfo(serviceObjects, cancellationToken, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        _appResources.AddRange(containerNetworkServices.Select(service => service.ServiceResource));
        DcpModelUtilities.AddContainerTunnelAllocatedEndpoints(
            hostResources.Select(resource => resource.Resource),
            _appResources,
            await GetContainerHostNameAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<AppResource<ContainerNetworkTunnelProxy>> CreateTunnelProxyResourceAsync(
        IDcpObjectFactory factory,
        List<TunnelConfiguration>? tunnels,
        CancellationToken cancellationToken)
    {
        Debug.Assert(_options.Value.EnableAspireContainerTunnel, "This method should only be called if the container tunnel feature is enabled.");
        Debug.Assert(!_appResources.Get().OfType<AppResource<ContainerNetworkTunnelProxy>>().Any(), "This method should only be called if a tunnel proxy resource hasn't already been created.");

        var tunnelProxy = ContainerNetworkTunnelProxy.Create(GetTunnelProxyResourceName());
        tunnelProxy.Spec.ContainerNetworkName = KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value;
        tunnelProxy.Spec.Aliases = [await GetContainerHostNameAsync(cancellationToken).ConfigureAwait(false)];
        tunnelProxy.Spec.Tunnels = tunnels;
        var tunnelAppResource = new AppResource<ContainerNetworkTunnelProxy>(tunnelProxy);
        _appResources.Add(tunnelAppResource);

        await factory.CreateDcpObjectsAsync([tunnelProxy], cancellationToken).ConfigureAwait(false);
        await WaitForTunnelProxyAsync(tunnelProxy, factory, cancellationToken).ConfigureAwait(false);

        return tunnelAppResource;
    }

    private async Task WaitForTunnelProxyAsync(
        ContainerNetworkTunnelProxy tunnelProxy,
        IDcpObjectFactory factory,
        CancellationToken cancellationToken)
    {
        // Container tunnel initialization can take a while if the container tunnel image needs to be built,
        // especially if the required image pull is slow, hence 10 minute timeout here.
        var observedProxies = await factory.WaitForStateAsync(
            [tunnelProxy],
            proxy =>
            {
                var status = proxy.Status;
                if (string.Equals(status?.State, ContainerNetworkTunnelProxyState.Failed, StringComparison.Ordinal))
                {
                    return ContainerNetworkTunnelProxyState.Failed;
                }

                if (status is not null && string.Equals(status.State, ContainerNetworkTunnelProxyState.Running, StringComparison.Ordinal))
                {
                    return ContainerNetworkTunnelProxyState.Running;
                }

                return null;
            },
            [ContainerNetworkTunnelProxyState.Running, ContainerNetworkTunnelProxyState.Failed],
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);

        var observedProxy = observedProxies.Single();
        tunnelProxy.Status = observedProxy.Status;

        var failed = string.Equals(observedProxy.Status?.State, ContainerNetworkTunnelProxyState.Failed, StringComparison.Ordinal);
        var observedStatus = observedProxy.Status;
        var running = observedStatus is not null &&
            string.Equals(observedStatus.State, ContainerNetworkTunnelProxyState.Running, StringComparison.Ordinal);

        const string noDetailsAvailable = "(no additional error details available)";
        if (failed)
        {
            _logger.LogError(
                "Container network tunnel proxy '{Name}' failed: {Details}",
                observedProxy.Metadata.Name,
                observedProxy.Status?.Message ?? noDetailsAvailable);
        }

        if (failed || !running)
        {
            var details = failed
                ? $"'{observedProxy.Metadata.Name}': {observedProxy.Status?.Message ?? noDetailsAvailable}"
                : $"'{observedProxy.Metadata.Name}': did not reach a stable state (current state: '{observedProxy.Status?.State ?? "(unknown)"}')";
            throw new DistributedApplicationException(
                $"One or more container network tunnel proxies did not start successfully: {details}");
        }
    }

    private async Task<string> GetContainerHostNameAsync(CancellationToken cancellationToken)
    {
        if (_configuration["AppHost:ContainerHostname"] is string hostname)
        {
            return hostname;
        }

        if (_options.Value.EnableAspireContainerTunnel)
        {
            return KnownHostNames.DefaultContainerTunnelHostName;
        }

        var dcpInfo = await _dcpDependencyCheckService
            .GetDcpInfoAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return dcpInfo?.Containers?.HostName ?? KnownHostNames.DockerDesktopHostBridge;
    }

    private string GetTunnelProxyResourceName()
    {
        Debug.Assert(_options.Value.EnableAspireContainerTunnel, "This method should only be called if the container tunnel feature is enabled.");
        return KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value + "-tunnelproxy";
    }

    // The application name is only a suffix of the physical network name, so invalid characters can be omitted.
    private static string NormalizeApplicationName(string applicationName)
    {
        if (string.IsNullOrEmpty(applicationName))
        {
            return applicationName;
        }

        applicationName = ApplicationNameRegex().Match(applicationName) switch
        {
            Match { Success: true } match => match.Groups["name"].Value,
            _ => applicationName
        };

        if (string.IsNullOrEmpty(applicationName))
        {
            return applicationName;
        }

        var normalizedName = new StringBuilder();
        for (var i = 0; i < applicationName.Length; i++)
        {
            if ((applicationName[i] is >= 'a' and <= 'z') ||
                (applicationName[i] is >= 'A' and <= 'Z') ||
                (applicationName[i] is >= '0' and <= '9') ||
                (applicationName[i] is '_' or '-' or '.'))
            {
                normalizedName.Append(applicationName[i]);
            }
        }

        return normalizedName.ToString();
    }

    [GeneratedRegex("""^(?<name>.+?)\.?AppHost$""", RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationNameRegex();
}
