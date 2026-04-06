// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001

using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// A host resource with endpoints that containers may depend on.
/// </summary>
internal record struct HostResourceWithEndpoints(
    IResourceWithEndpoints Resource,
    IEnumerable<EndpointAnnotation> Endpoints)
{
    internal static HostResourceWithEndpoints? Create(IResource resource)
    {
        if (resource is IResourceWithEndpoints rwe && !resource.IsContainer())
        {
            var endpoints = resource.Annotations.OfType<EndpointAnnotation>();
            if (endpoints.Any())
            {
                return new HostResourceWithEndpoints(rwe, endpoints);
            }
        }

        return null;
    }
}

/// <summary>
/// Handles preparation and creation of Container, ContainerExec, ContainerNetwork,
/// and ContainerNetworkTunnelProxy DCP resources.
/// </summary>
internal sealed class ContainerCreator : IObjectCreator<Container, ContainerCreationContext>, IObjectCreator<ContainerExec, EmptyCreationContext>
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<DcpOptions> _options;
    private readonly DcpNameGenerator _nameGenerator;
    private readonly DistributedApplicationModel _model;
    private readonly DistributedApplicationExecutionContext _executionContext;
    private readonly ResourceLoggerService _loggerService;
    private readonly IDcpDependencyCheckService _dcpDependencyCheckService;
    private readonly ILogger<ContainerCreator> _logger;
    private readonly string _normalizedApplicationName;
    private readonly DcpAppResourceStore _appResources;

    public ContainerCreator(
        IConfiguration configuration,
        IOptions<DcpOptions> options,
        DcpNameGenerator nameGenerator,
        DistributedApplicationModel model,
        DistributedApplicationExecutionContext executionContext,
        ResourceLoggerService loggerService,
        IDcpDependencyCheckService dcpDependencyCheckService,
        IHostEnvironment hostEnvironment,
        ILogger<ContainerCreator> logger,
        DcpAppResourceStore appResources)
    {
        _configuration = configuration;
        _options = options;
        _nameGenerator = nameGenerator;
        _model = model;
        _executionContext = executionContext;
        _loggerService = loggerService;
        _dcpDependencyCheckService = dcpDependencyCheckService;
        _logger = logger;
        _normalizedApplicationName = DcpExecutor.NormalizeApplicationName(hostEnvironment.ApplicationName);
        _appResources = appResources;
    }

    private async Task<string> GetContainerHostNameAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration["AppHost:ContainerHostname"] is string hostname)
        {
            return hostname;
        }

        if (_options.Value.EnableAspireContainerTunnel)
        {
            return KnownHostNames.DefaultContainerTunnelHostName;
        }

        var dcpInfo = await _dcpDependencyCheckService.GetDcpInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return dcpInfo?.Containers?.HostName ?? KnownHostNames.DockerDesktopHostBridge;
    }

    internal void PrepareContainerNetworks()
    {
        var containerResources = _model.Resources.Where(mr => mr.IsContainer());
        if (!containerResources.Any()) { return; }

        var network = ContainerNetwork.Create(KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value);
        if (containerResources.Any(cr => cr.GetContainerLifetimeType() == ContainerLifetime.Persistent))
        {
            network.Spec.Persistent = true;
            network.Spec.NetworkName = $"{DcpExecutor.DefaultAspirePersistentNetworkName}-{_nameGenerator.GetProjectHashSuffix()}";
        }
        else
        {
            network.Spec.NetworkName = $"{DcpExecutor.DefaultAspireNetworkName}-{DcpNameGenerator.GetRandomNameSuffix()}";
        }

        if (!string.IsNullOrEmpty(_normalizedApplicationName))
        {
            var shortApplicationName = _normalizedApplicationName.Length < 32 ? _normalizedApplicationName : _normalizedApplicationName.Substring(0, 32);
            network.Spec.NetworkName += $"-{shortApplicationName}";
        }

        _appResources.Add(new AppResource<ContainerNetwork>(network));
    }

    public IEnumerable<RenderedModelResource<Container>> PrepareObjects()
    {
        var modelContainerResources = _model.GetContainerResources();
        var result = new List<RenderedModelResource<Container>>();

        foreach (var container in modelContainerResources)
        {
            if (!container.TryGetContainerImageName(out var containerImageName))
            {
                throw new InvalidOperationException();
            }

            EnsureRequiredAnnotations(container);

            var containerObjectInstance = DcpExecutor.GetDcpInstance(container, instanceIndex: 0);
            var ctr = Container.Create(containerObjectInstance.Name, containerImageName);

            ctr.Spec.ContainerName = containerObjectInstance.Name;

            if (container.GetContainerLifetimeType() == ContainerLifetime.Persistent)
            {
                ctr.Spec.Persistent = true;
            }

            if (container.TryGetContainerImagePullPolicy(out var pullPolicy))
            {
                ctr.Spec.PullPolicy = pullPolicy switch
                {
                    ImagePullPolicy.Default => null,
                    ImagePullPolicy.Always => ContainerPullPolicy.Always,
                    ImagePullPolicy.Missing => ContainerPullPolicy.Missing,
                    ImagePullPolicy.Never => ContainerPullPolicy.Never,
                    _ => throw new InvalidOperationException($"Unknown pull policy '{Enum.GetName(typeof(ImagePullPolicy), pullPolicy)}' for container '{container.Name}'")
                };
            }

            ctr.Annotate(CustomResource.ResourceNameAnnotation, container.Name);
            ctr.Annotate(CustomResource.OtelServiceNameAnnotation, container.Name);
            ctr.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, containerObjectInstance.Suffix);
            DcpExecutor.SetInitialResourceState(container, ctr);

            var aanns = container.Annotations.OfType<ContainerNetworkAliasAnnotation>().ToImmutableArray();
            if (aanns.Any(a => a.Network != KnownNetworkIdentifiers.DefaultAspireContainerNetwork))
            {
                throw new InvalidOperationException("Custom container networks are not supported yet.");
            }

            ctr.Spec.Networks = new List<ContainerNetworkConnection>
            {
                new ContainerNetworkConnection
                {
                    Name = KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value,
                    Aliases = aanns.Select(a => a.Alias)
                                .Prepend($"{container.Name}.dev.internal")
                                .Prepend(container.Name)
                                .ToList()
                }
            };

            if (container.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _))
            {
                ctr.Spec.Start = false;
            }

            var containerAppResource = new RenderedModelResource<Container>(container, ctr);
            DcpModelUtilities.AddServicesProducedInfo(containerAppResource, _appResources.Get());
            _appResources.Add(containerAppResource);
            result.Add(containerAppResource);
        }

        return result;
    }

    public bool IsReadyToCreate(RenderedModelResource<Container> resource, ContainerCreationContext cctx)
    {
        // Containers are always "created" (submitted to DCP), they just get Spec.Start = false initially
        // if explicit startup is used.
        return true;
    }

    public async Task CreateObjectAsync(RenderedModelResource<Container> cr, ContainerCreationContext cctx, ILogger logger, IDcpObjectFactory factory, CancellationToken cancellationToken)
    {
        var signalServicesSpecReadyOnce = ConcurrencyUtils.Once(() => cctx.ContainerServicesSpecReady.Signal());

        try
        {
            var hostDependencies = (await GetHostDependenciesAsync(cr.ModelResource, cancellationToken).ConfigureAwait(false)).ToImmutableArray();

            if (hostDependencies.Any())
            {
                await CreateTunnelDependentContainerAsync(cr, hostDependencies, cctx, signalServicesSpecReadyOnce, factory, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                signalServicesSpecReadyOnce();
                await BuildAndCreateContainerAsync(cr, logger, factory, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            signalServicesSpecReadyOnce();
        }
    }

    internal void PrepareContainerExecutables()
    {
        var modelContainerExecutableResources = _model.GetContainerExecutableResources();
        foreach (var containerExecutable in modelContainerExecutableResources)
        {
            EnsureRequiredAnnotations(containerExecutable);
            var exeInstance = DcpExecutor.GetDcpInstance(containerExecutable, instanceIndex: 0);

            var containerDcpName = containerExecutable.TargetContainerResource!.GetResolvedResourceName();

            var containerExec = ContainerExec.Create(
                name: exeInstance.Name,
                containerName: containerDcpName,
                command: containerExecutable.Command,
                args: containerExecutable.Args?.ToList(),
                workingDirectory: containerExecutable.WorkingDirectory);

            containerExec.Annotate(CustomResource.OtelServiceNameAnnotation, containerExecutable.Name);
            containerExec.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, exeInstance.Suffix);
            containerExec.Annotate(CustomResource.ResourceNameAnnotation, containerExecutable.Name);
            DcpExecutor.SetInitialResourceState(containerExecutable, containerExec);

            var exeAppResource = new RenderedModelResource<ContainerExec>(containerExecutable, containerExec);
            _appResources.Add(exeAppResource);
        }
    }

    private async Task BuildAndCreateContainerAsync(RenderedModelResource<Container> cr, ILogger logger, IDcpObjectFactory factory, CancellationToken cToken)
    {
        cToken.ThrowIfCancellationRequested();

        var dcpContainer = cr.DcpResource;
        var modelContainer = cr.ModelResource;

        await ApplyBuildArgumentsAsync(dcpContainer, cr.ModelResource, _executionContext.ServiceProvider, cToken).ConfigureAwait(false);

        var spec = dcpContainer.Spec;

        if (cr.ServicesProduced.Count > 0)
        {
            spec.Ports = BuildContainerPorts(cr);
        }

        spec.VolumeMounts = BuildContainerMounts(cr.ModelResource);

        var (runArgs, failedToApplyRunArgs) = await BuildRunArgsAsync(logger, cr.ModelResource, cToken).ConfigureAwait(false);
        if (failedToApplyRunArgs)
        {
            throw new FailedToApplyEnvironmentException();
        }
        spec.RunArgs = runArgs;

        var (configuration, pemCertificates, createFiles) = await BuildContainerConfiguration(cr, logger, cToken).ConfigureAwait(false);
        if (configuration.Exception is not null)
        {
            throw new FailedToApplyEnvironmentException($"Failed to apply configuration to container {cr.ModelResource.Name}", configuration.Exception);
        }

        var args = configuration.Arguments.Select(a => a.Value);
        if (modelContainer is ContainerResource { ShellExecution: true })
        {
            spec.Args = ["-c", $"{string.Join(' ', args)}"];
        }
        else
        {
            spec.Args = args.ToList();
        }
        dcpContainer.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation, configuration.Arguments.Select(a => new AppLaunchArgumentAnnotation(a.Value, isSensitive: a.IsSensitive)));

        spec.Env = configuration.EnvironmentVariables.Select(kvp => new EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList();
        spec.CreateFiles = createFiles;
        if (modelContainer is ContainerResource containerResource)
        {
            spec.Command = containerResource.Entrypoint;
        }
        spec.PemCertificates = pemCertificates;

        var dcpInfo = await _dcpDependencyCheckService.GetDcpInfoAsync(cancellationToken: cToken).ConfigureAwait(false);
        if (dcpInfo is not null)
        {
            DcpDependencyCheck.CheckDcpInfoAndLogErrors(logger, _options.Value, dcpInfo);
        }

        await factory.CreateDcpObjectsAsync(new[] { dcpContainer }, cToken).ConfigureAwait(false);

        var containerExes = _appResources.Get().OfType<RenderedModelResource<ContainerExec>>().Where(ar => ar.DcpResource.Spec.ContainerName == dcpContainer.Metadata.Name).ToArray();
        if (containerExes.Length > 0)
        {
            IObjectCreator<ContainerExec, EmptyCreationContext> containerExecCreator = this;
            await factory.CreateRenderedResourcesAsync(containerExecCreator, containerExes, EmptyCreationContext.s_instance, cToken).ConfigureAwait(false);
        }
    }

    IEnumerable<RenderedModelResource<ContainerExec>> IObjectCreator<ContainerExec, EmptyCreationContext>.PrepareObjects()
        => _appResources.Get().OfType<RenderedModelResource<ContainerExec>>();

    bool IObjectCreator<ContainerExec, EmptyCreationContext>.IsReadyToCreate(RenderedModelResource<ContainerExec> resource, EmptyCreationContext context)
        => true;

    async Task IObjectCreator<ContainerExec, EmptyCreationContext>.CreateObjectAsync(RenderedModelResource<ContainerExec> er, EmptyCreationContext context, ILogger _, IDcpObjectFactory factory, CancellationToken cancellationToken)
    {
        if (er.DcpResource is not ContainerExec containerExe)
        {
            throw new InvalidOperationException($"Expected an {nameof(ContainerExec)} resource, but got {er.DcpResourceKind} instead");
        }

        await factory.CreateDcpObjectsAsync([containerExe], cancellationToken).ConfigureAwait(false);
    }

    internal IEnumerable<ContainerNetworkService> CreateContainerNetworkServicesForHostResource(HostResourceWithEndpoints re)
    {
        var resourceLogger = _loggerService.GetLogger(re.Resource);
        var services = new List<ContainerNetworkService>();
        var useTunnel = _options.Value.EnableAspireContainerTunnel;
        string tunnelProxyName = useTunnel ? GetTunnelProxyResourceName() : "";

        foreach (var endpoint in re.Endpoints)
        {
            var (serviceName, isNew) = _nameGenerator.GetServiceName(re.Resource, endpoint, KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
            if (!isNew)
            {
                continue;
            }

            if (useTunnel && endpoint.Protocol != ProtocolType.Tcp)
            {
                resourceLogger.LogWarning("Host endpoint '{EndpointName}' on resource '{HostResource}' is referenced by a container resource, but the endpoint is using a network protocol '{Protocol}' other than TCP. Only TCP is supported for container-to-host references.", endpoint.Name, re.Resource.Name, endpoint.Protocol);
                continue;
            }

            var svc = Service.Create(serviceName);
            svc.Spec.AddressAllocationMode = AddressAllocationModes.Proxyless;
            svc.Spec.Protocol = PortProtocol.FromProtocolType(endpoint.Protocol);

            var serverSvc = _appResources.Get().OfType<ServiceWithModelResource>().FirstOrDefault(swr =>
                StringComparers.ResourceName.Equals(swr.ModelResource.Name, re.Resource.Name) &&
                StringComparers.EndpointAnnotationName.Equals(swr.EndpointAnnotation.Name, endpoint.Name)
            );
            if (serverSvc is null)
            {
                throw new InvalidDataException($"Host endpoint '{endpoint.Name}' on resource '{re.Resource.Name}' should have an associated DCP Service resource already set up");
            }

            TunnelConfiguration? tunnelConfig = null;
            if (useTunnel)
            {
                tunnelConfig = new TunnelConfiguration
                {
                    Name = serviceName,
                    ServerServiceName = serverSvc.DcpResource.Metadata.Name,
                    ServerServiceNamespace = string.Empty,
                    ClientServiceName = svc.Metadata.Name,
                    ClientServiceNamespace = string.Empty
                };
            }

            svc.Annotate(CustomResource.ResourceNameAnnotation, re.Resource.Name);
            svc.Annotate(CustomResource.EndpointNameAnnotation, endpoint.Name);
            svc.Annotate(CustomResource.ContainerNetworkAnnotation, KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value);
            svc.Annotate(CustomResource.PrimaryServiceNameAnnotation, serverSvc.DcpResource.Metadata.Name);
            svc.Annotate(CustomResource.ContainerTunnelInstanceName, tunnelProxyName);

            var svcAppResource = new AppResource<Service>(svc);
            services.Add(new ContainerNetworkService { ServiceResource = svcAppResource, TunnelConfig = tunnelConfig });
        }

        return services;
    }

    internal async Task<AppResource<ContainerNetworkTunnelProxy>> CreateTunnelProxyResourceAsync(List<TunnelConfiguration>? tunnels, CancellationToken cancellationToken = default)
    {
        Debug.Assert(_options.Value.EnableAspireContainerTunnel, "This method should only be called if the container tunnel feature is enabled.");
        Debug.Assert(!_appResources.Get().OfType<AppResource<ContainerNetworkTunnelProxy>>().Any(), "This method should only be called if a tunnel proxy resource hasn't already been created.");

        var tunnelProxy = ContainerNetworkTunnelProxy.Create(GetTunnelProxyResourceName());
        tunnelProxy.Spec.ContainerNetworkName = KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value;
        tunnelProxy.Spec.Aliases = [await GetContainerHostNameAsync(cancellationToken).ConfigureAwait(false)];
        tunnelProxy.Spec.Tunnels = tunnels;
        var tunnelAppResource = new AppResource<ContainerNetworkTunnelProxy>(tunnelProxy);
        _appResources.Add(tunnelAppResource);
        return tunnelAppResource;
    }

    /// <summary>
    /// Creates the container tunnel: reads tunnel service specs from the container creation context,
    /// creates the corresponding DCP service and tunnel proxy objects, and waits for their addresses.
    /// </summary>
    internal async Task CreateTunnelAsync(ContainerCreationContext cctx, IDcpObjectFactory factory, CancellationToken cancellationToken)
    {
        // Container creation tasks need to figure out dependencies of each container 
        // and then create Service and TunnelConfiguration definitions for each of them.
        cctx.ContainerServicesSpecReady.Wait(cancellationToken);
        cctx.ContainerServicesChan.Writer.Complete();

        // Now create the container network services for the host resources, update the tunnel, and advertise AllocatedEndpoints.
        var containerNetworkServices = cctx.ContainerServicesChan.Reader.ReadAllAsync(cancellationToken).ToBlockingEnumerable(cancellationToken).ToArray();
        _appResources.AddRange(containerNetworkServices.Select(cns => cns.ServiceResource));
        var serviceObjects = containerNetworkServices.Select(cns => cns.ServiceResource.DcpResource).ToArray();
        await factory.CreateDcpObjectsAsync(serviceObjects, cancellationToken).ConfigureAwait(false);

        var tunnels = containerNetworkServices.Where(s => s.TunnelConfig is not null).Select(s => s.TunnelConfig!).ToList();
        Debug.Assert(tunnels.Count == containerNetworkServices.Length, "Each tunneled service should have a tunnel config");
        await CreateTunnelProxyResourceAsync(tunnels, cancellationToken).ConfigureAwait(false);

        // Create all ContainerNetworkTunnelProxy objects that have been prepared.
        var tunnelProxies = _appResources.Get().OfType<AppResource<ContainerNetworkTunnelProxy>>().Select(r => r.DcpResource);
        await factory.CreateDcpObjectsAsync(tunnelProxies, cancellationToken).ConfigureAwait(false);

        // Container tunnel initialization can take a while if the container tunnel image needs to be built,
        // especially if the required image pull is slow, hence 10 minute timeout here.
        await factory.UpdateWithEffectiveAddressInfo(serviceObjects, cancellationToken, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
    }

    internal async Task<IEnumerable<HostResourceWithEndpoints>> GetHostDependenciesAsync(IResource resource, CancellationToken cancellationToken)
    {
        var allDependencies = await ResourceExtensions.GetResourceDependenciesAsync(
            resource,
            _executionContext,
            new ResourceDependencyDiscoveryOptions
            {
                DiscoveryMode = ResourceDependencyDiscoveryMode.DirectOnly,
                CacheAnnotationCallbackResults = true
            },
            cancellationToken
        ).ConfigureAwait(false);

        List<HostResourceWithEndpoints> hostDependencies = [.. allDependencies.Select(HostResourceWithEndpoints.Create).OfType<HostResourceWithEndpoints>()];

        if (resource.TryGetAnnotationsOfType<OtlpExporterAnnotation>(out _))
        {
            var maybeDashboard = _model.Resources.Where(r => StringComparers.ResourceName.Equals(r.Name, KnownResourceNames.AspireDashboard))
                    .Select(HostResourceWithEndpoints.Create).FirstOrDefault();
            if (maybeDashboard is HostResourceWithEndpoints dashboardResource)
            {
                hostDependencies.Add(dashboardResource);
            }
        }

        return hostDependencies;
    }

    internal async Task CreateTunnelDependentContainerAsync(RenderedModelResource<Container> cr, ImmutableArray<HostResourceWithEndpoints> hostDependencies, ContainerCreationContext cctx, Action signalServicesSpecReadyOnce, IDcpObjectFactory factory, CancellationToken cToken)
    {
        cToken.ThrowIfCancellationRequested();

        List<ContainerNetworkService> newServices = [];
        foreach (var dep in hostDependencies)
        {
            var cnetServices = CreateContainerNetworkServicesForHostResource(dep);
            newServices.AddRange(cnetServices);
        }
        if (newServices.Count > 0)
        {
            foreach (var s in newServices)
            {
                await cctx.ContainerServicesChan.Writer.WriteAsync(s, cToken).ConfigureAwait(false);
            }
        }

        signalServicesSpecReadyOnce();
        await cctx.CreateTunnel.ConfigureAwait(false);

        await BuildAndCreateContainerAsync(cr, _loggerService.GetLogger(cr.ModelResource), factory, cToken).ConfigureAwait(false);
    }

    private string GetTunnelProxyResourceName()
    {
        Debug.Assert(_options.Value.EnableAspireContainerTunnel, "This method should only be called if the container tunnel feature is enabled.");
        return KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value + "-tunnelproxy";
    }

    private async Task<(IExecutionConfigurationResult, ContainerPemCertificates?, List<ContainerCreateFileSystem>?)>
    BuildContainerConfiguration(RenderedModelResource<Container> cr, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        var certificatesDestination = ContainerCertificatePathsAnnotation.DefaultCustomCertificatesDestination;
        var bundlePaths = ContainerCertificatePathsAnnotation.DefaultCertificateBundlePaths.ToList();
        var certificateDirsPaths = ContainerCertificatePathsAnnotation.DefaultCertificateDirectoriesPaths.ToList();

        if (cr.ModelResource.TryGetLastAnnotation<ContainerCertificatePathsAnnotation>(out var pathsAnnotation))
        {
            certificatesDestination = pathsAnnotation.CustomCertificatesDestination ?? certificatesDestination;
            bundlePaths = pathsAnnotation.DefaultCertificateBundles ?? bundlePaths;
            certificateDirsPaths = pathsAnnotation.DefaultCertificateDirectories ?? certificateDirsPaths;
        }

        var serverAuthCertificatesBasePath = $"{certificatesDestination}/private";

        var configuration = await ExecutionConfigurationBuilder.Create(cr.ModelResource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .WithCertificateTrustConfig(scope =>
            {
                var dirs = new List<string> { certificatesDestination + "/certs" };
                if (scope == CertificateTrustScope.Append)
                {
                    dirs.AddRange(certificateDirsPaths!);
                }

                return new()
                {
                    CertificateBundlePath = ReferenceExpression.Create($"{certificatesDestination}/cert.pem"),
                    CertificateDirectoriesPath = ReferenceExpression.Create($"{string.Join(':', dirs)}"),
                    RootCertificatesPath = certificatesDestination,
                    IsContainer = true,
                };
            })
            .WithHttpsCertificateConfig(cert => new()
            {
                CertificatePath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.crt"),
                KeyPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.key"),
                PfxPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.pfx"),
            })
            .AddExecutionConfigurationGatherer(new OtlpEndpointReferenceGatherer())
            .BuildAsync(_executionContext, resourceLogger, cancellationToken)
            .ConfigureAwait(false);

        List<ContainerFileSystemEntry> customBundleFiles = new();

        ContainerPemCertificates? pemCertificates = null;
        if (configuration.TryGetAdditionalData<CertificateTrustExecutionConfigurationData>(out var certificateTrustConfiguration)
            && certificateTrustConfiguration.Scope != CertificateTrustScope.None
            && certificateTrustConfiguration.Certificates.Count > 0)
        {
            pemCertificates = new ContainerPemCertificates
            {
                Certificates = CertificateUtilities.BuildPemCertificateList(certificateTrustConfiguration.Certificates),
                Destination = certificatesDestination,
                ContinueOnError = true,
            };

            if (certificateTrustConfiguration.Scope != CertificateTrustScope.Append)
            {
                pemCertificates.OverwriteBundlePaths = bundlePaths;
            }

            foreach (var bundleFactory in certificateTrustConfiguration.CustomBundlesFactories)
            {
                var bundleId = bundleFactory.Key;
                var bundleBytes = await bundleFactory.Value(certificateTrustConfiguration.Certificates, cancellationToken).ConfigureAwait(false);

                customBundleFiles.Add(new ContainerFileSystemEntry
                {
                    Name = bundleId,
                    Type = ContainerFileSystemEntryType.File,
                    RawContents = Convert.ToBase64String(bundleBytes),
                });
            }
        }

        var buildCreateFilesContext = new BuildCreateFilesContext
        {
            Resource = cr.ModelResource,
            CertificateTrustScope = certificateTrustConfiguration?.Scope ?? CertificateTrustScope.None,
            CertificateTrustBundlePath = $"{certificatesDestination}/cert.pem",
        };

        if (configuration.TryGetAdditionalData<HttpsCertificateExecutionConfigurationData>(out var tlsCertificateConfiguration))
        {
            var thumbprint = tlsCertificateConfiguration.Certificate.Thumbprint;
            buildCreateFilesContext.HttpsCertificateContext = new ContainerFileSystemCallbackHttpsCertificateContext
            {
                CertificatePath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{thumbprint}.crt"),
                KeyPath = tlsCertificateConfiguration.KeyPathReference,
                PfxPath = tlsCertificateConfiguration.PfxPathReference,
                Password = tlsCertificateConfiguration.Password,
            };
        }

        var createFiles = await BuildCreateFilesAsync(buildCreateFilesContext, cancellationToken).ConfigureAwait(false);

        if (customBundleFiles.Count > 0)
        {
            createFiles.Add(new ContainerCreateFileSystem
            {
                Destination = certificatesDestination,
                Entries = [
                    new ContainerFileSystemEntry
                    {
                        Name = "bundles",
                        Type = ContainerFileSystemEntryType.Directory,
                        Entries = customBundleFiles,
                    },
                ],
            });
        }

        if (tlsCertificateConfiguration is not null)
        {
            var thumbprint = tlsCertificateConfiguration.Certificate.Thumbprint;
            var publicCertificatePem = tlsCertificateConfiguration.Certificate.ExportCertificatePem();
            (var keyPem, var pfxBytes) = await DeveloperCertificateService.GetKeyMaterialAsync(
                tlsCertificateConfiguration.Certificate,
                tlsCertificateConfiguration.Password,
                tlsCertificateConfiguration.IsKeyPathReferenced,
                tlsCertificateConfiguration.IsPfxPathReferenced,
                cancellationToken
            ).ConfigureAwait(false);

            var certificateFiles = new List<ContainerFileSystemEntry>()
            {
                new ContainerFileSystemEntry
                {
                    Name = thumbprint + ".crt",
                    Type = ContainerFileSystemEntryType.File,
                    Contents = new string(publicCertificatePem),
                }
            };

            if (keyPem is not null)
            {
                certificateFiles.Add(new ContainerFileSystemEntry
                {
                    Name = thumbprint + ".key",
                    Type = ContainerFileSystemEntryType.File,
                    Contents = new string(keyPem),
                });

                Array.Clear(keyPem, 0, keyPem.Length);
            }

            if (pfxBytes is not null)
            {
                certificateFiles.Add(new ContainerFileSystemEntry
                {
                    Name = thumbprint + ".pfx",
                    Type = ContainerFileSystemEntryType.File,
                    RawContents = Convert.ToBase64String(pfxBytes),
                });

                Array.Clear(pfxBytes, 0, pfxBytes.Length);
            }

            createFiles.Add(new ContainerCreateFileSystem
            {
                Destination = serverAuthCertificatesBasePath,
                Entries = certificateFiles,
            });
        }

        return (configuration, pemCertificates, createFiles);
    }

    private async Task<List<ContainerCreateFileSystem>> BuildCreateFilesAsync(BuildCreateFilesContext context, CancellationToken cancellationToken)
    {
        var createFiles = new List<ContainerCreateFileSystem>();

        if (context.Resource.TryGetAnnotationsOfType<ContainerFileSystemCallbackAnnotation>(out var createFileAnnotations))
        {
            foreach (var a in createFileAnnotations)
            {
                var entries = await a.Callback(
                    new()
                    {
                        Model = context.Resource,
                        ServiceProvider = _executionContext.ServiceProvider,
                        HttpsCertificateContext = context.HttpsCertificateContext,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (entries?.Any() != true)
                {
                    continue;
                }

                createFiles.Add(new ContainerCreateFileSystem
                {
                    Destination = a.DestinationPath,
                    DefaultOwner = a.DefaultOwner,
                    DefaultGroup = a.DefaultGroup,
                    Umask = (int?)a.Umask,
                    Entries = entries.Select(e => e.ToContainerFileSystemEntry()).ToList(),
                });
            }
        }

        return createFiles;
    }

    private async Task<(List<string>, bool)> BuildRunArgsAsync(ILogger resourceLogger, IResource modelResource, CancellationToken cancellationToken)
    {
        var failedToApplyArgs = false;
        var runArgs = new List<string>();

        await modelResource.ProcessContainerRuntimeArgValues(
            _executionContext,
            (a, ex) =>
            {
                if (ex is not null)
                {
                    failedToApplyArgs = true;
                    resourceLogger.LogCritical(ex, "Failed to apply argument value '{ArgKey}'. A dependency may have failed to start.", a);
                    _logger.LogDebug(ex, "Failed to apply argument value '{ArgKey}' to '{ResourceName}'. A dependency may have failed to start.", a, modelResource.Name);
                }
                else if (a is string s)
                {
                    runArgs.Add(s);
                }
            },
            resourceLogger,
            cancellationToken).ConfigureAwait(false);

        return (runArgs, failedToApplyArgs);
    }

    private static async Task ApplyBuildArgumentsAsync(Container dcpContainerResource, IResource modelContainerResource, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        if (modelContainerResource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault() is { } dockerfileBuildAnnotation)
        {
            await DockerfileHelper.ExecuteDockerfileFactoryAsync(dockerfileBuildAnnotation, modelContainerResource, serviceProvider, cancellationToken).ConfigureAwait(false);

            var dcpBuildArgs = new List<EnvVar>();

            foreach (var buildArgument in dockerfileBuildAnnotation.BuildArguments)
            {
                var valueString = buildArgument.Value switch
                {
                    string stringValue => stringValue,
                    IValueProvider valueProvider => await valueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false),
                    bool boolValue => boolValue ? "true" : "false",
                    null => null,
                    _ => buildArgument.Value.ToString()
                };

                dcpBuildArgs.Add(new EnvVar() { Name = buildArgument.Key, Value = valueString });
            }

            var dcpBuildSecrets = new List<BuildContextSecret>();

            foreach (var buildSecret in dockerfileBuildAnnotation.BuildSecrets)
            {
                var valueString = buildSecret.Value switch
                {
                    FileInfo filePath => filePath.FullName,
                    IValueProvider valueProvider => await valueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false),
                    _ => throw new InvalidOperationException("Build secret can only be a parameter or a file.")
                };

                if (buildSecret.Value is FileInfo)
                {
                    dcpBuildSecrets.Add(new BuildContextSecret { Id = buildSecret.Key, Type = "file", Source = valueString });
                }
                else
                {
                    dcpBuildSecrets.Add(new BuildContextSecret { Id = buildSecret.Key, Type = "env", Value = valueString });
                }
            }

            dcpContainerResource.Spec.Build = new()
            {
                Context = dockerfileBuildAnnotation.ContextPath,
                Dockerfile = dockerfileBuildAnnotation.DockerfilePath,
                Stage = dockerfileBuildAnnotation.Stage,
                Args = dcpBuildArgs,
                Secrets = dcpBuildSecrets
            };
        }
    }

    private static List<ContainerPortSpec> BuildContainerPorts(RenderedModelResource<Container> cr)
    {
        var ports = new List<ContainerPortSpec>();

        foreach (var sp in cr.ServicesProduced)
        {
            var ea = sp.EndpointAnnotation;

            var portSpec = new ContainerPortSpec()
            {
                ContainerPort = ea.TargetPort,
            };

            if (!ea.IsProxied && ea.Port is int)
            {
                portSpec.HostPort = ea.Port;
            }

            switch (sp.EndpointAnnotation.Protocol)
            {
                case ProtocolType.Tcp:
                    portSpec.Protocol = PortProtocol.TCP;
                    break;
                case ProtocolType.Udp:
                    portSpec.Protocol = PortProtocol.UDP;
                    break;
            }

            if (sp.EndpointAnnotation.TargetHost != KnownHostNames.Localhost)
            {
                portSpec.HostIP = sp.EndpointAnnotation.TargetHost;
            }

            ports.Add(portSpec);
        }

        return ports;
    }

    private static List<VolumeMount> BuildContainerMounts(IResource container)
    {
        var volumeMounts = new List<VolumeMount>();

        if (container.TryGetContainerMounts(out var containerMounts))
        {
            foreach (var mount in containerMounts)
            {
                volumeMounts.Add(new VolumeMount
                {
                    Source = mount.Source,
                    Target = mount.Target,
                    Type = mount.Type == ContainerMountType.BindMount ? VolumeMountType.Bind : VolumeMountType.Volume,
                    IsReadOnly = mount.IsReadOnly
                });
            }
        }

        return volumeMounts;
    }

    private void EnsureRequiredAnnotations(IResource resource)
    {
        resource.AddLifeCycleCommands();
        _nameGenerator.EnsureDcpInstancesPopulated(resource);
    }

    private class BuildCreateFilesContext
    {
        public required IResource Resource { get; init; }
        public CertificateTrustScope CertificateTrustScope { get; init; }
        public string? CertificateTrustBundlePath { get; set; }
        public string? CertificateTrustDirectoriesPath { get; set; }
        public ContainerFileSystemCallbackHttpsCertificateContext? HttpsCertificateContext { get; set; }
    }
}
