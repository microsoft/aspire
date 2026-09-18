// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001

using System.Collections.Immutable;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Handles preparation and creation of Container and ContainerExec DCP resources.
/// </summary>
internal sealed class ContainerCreator : IObjectCreator<Container, ContainerNetworkEndpointContext>, IObjectCreator<ContainerExec, EmptyCreationContext>
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<DcpOptions> _options;
    private readonly DcpNameGenerator _nameGenerator;
    private readonly DistributedApplicationModel _model;
    private readonly DistributedApplicationExecutionContext _executionContext;
    private readonly ResourceLoggerService _loggerService;
    private readonly IDcpDependencyCheckService _dcpDependencyCheckService;
    private readonly ILogger<ContainerCreator> _logger;
    private readonly DcpAppResourceStore _appResources;
    private readonly ContainerNetworkEndpointProvisioner _containerNetworkEndpointProvisioner;

    public ContainerCreator(
        IConfiguration configuration,
        IOptions<DcpOptions> options,
        DcpNameGenerator nameGenerator,
        DistributedApplicationModel model,
        DistributedApplicationExecutionContext executionContext,
        ResourceLoggerService loggerService,
        IDcpDependencyCheckService dcpDependencyCheckService,
        ILogger<ContainerCreator> logger,
        DcpAppResourceStore appResources,
        ContainerNetworkEndpointProvisioner containerNetworkEndpointProvisioner)
    {
        _configuration = configuration;
        _options = options;
        _nameGenerator = nameGenerator;
        _model = model;
        _executionContext = executionContext;
        _loggerService = loggerService;
        _dcpDependencyCheckService = dcpDependencyCheckService;
        _logger = logger;
        _appResources = appResources;
        _containerNetworkEndpointProvisioner = containerNetworkEndpointProvisioner;
    }

    public IEnumerable<RenderedModelResource<Container>> PrepareObjects()
    {
        var modelContainerResources = _model.GetContainerResources().ToArray();
        _containerNetworkEndpointProvisioner.ValidateContainerNameConflicts(modelContainerResources);

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

            if (container.GetLifetimeType() == Lifetime.Persistent)
            {
                ctr.Spec.Persistent = true;
                ApplyMonitorProcess(container, ctr.Spec);
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
            ctr.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, container.GetOtelServiceInstanceId(containerObjectInstance));
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

    private static void ApplyMonitorProcess(IResource resource, ContainerSpec spec)
    {
        if (resource.TryGetParentProcessLifetime(out var parentProcessId, out var parentProcessTimestamp))
        {
            spec.MonitorPid = parentProcessId;
            spec.MonitorTimestamp = parentProcessTimestamp;
        }
    }

    public bool IsReadyToCreate(RenderedModelResource<Container> resource, ContainerNetworkEndpointContext context)
    {
        return !DcpModelUtilities.ShouldDeferCreateForExplicitStart(resource.ModelResource, resource.DcpResource.Spec.Start);
    }

    public async Task CreateObjectAsync(RenderedModelResource<Container> cr, ContainerNetworkEndpointContext context, ILogger logger, IDcpObjectFactory factory, CancellationToken cancellationToken)
    {
        var hostDependencies = (await GetHostDependenciesAsync(cr.ModelResource, cancellationToken).ConfigureAwait(false)).ToImmutableArray();

        if (hostDependencies.Any())
        {
            await CreateHostDependentContainerAsync(cr, hostDependencies, context, factory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await BuildAndCreateContainerAsync(cr, logger, factory, cancellationToken).ConfigureAwait(false);
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
            containerExec.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, containerExecutable.GetOtelServiceInstanceId(exeInstance));
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

        await ApplyBuildArgumentsAsync(dcpContainer, cr.ModelResource, _executionContext, logger, cToken).ConfigureAwait(false);

        var spec = dcpContainer.Spec;

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

        // Environment callbacks can resolve proxyless endpoint ports and commit a fallback host port,
        // so build ports afterward.
        if (cr.ServicesProduced.Count > 0)
        {
            spec.Ports = BuildContainerPorts(cr);
        }

        var args = configuration.Arguments.ToList();
        if (modelContainer is ContainerResource { ShellExecution: true })
        {
            spec.Args = ["-c", $"{string.Join(' ', args.Select(a => a.Value))}"];
        }
        else
        {
            spec.Args = args.Select(a => a.Value).ToList();
        }

        var appLaunchArgumentAnnotations = modelContainer is ContainerResource { ShellExecution: true }
            ? args.Select(a => new AppLaunchArgumentAnnotation(a.Value, isSensitive: a.IsSensitive))
            : args.Select((a, index) => new AppLaunchArgumentAnnotation(a.Value, isSensitive: a.IsSensitive, effectiveArgumentIndex: index));
        dcpContainer.SetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation, appLaunchArgumentAnnotations);

        spec.Env = configuration.EnvironmentVariables.Select(kvp => new EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList();
        spec.CreateFiles = createFiles;
        if (modelContainer is ContainerResource containerResource)
        {
            spec.Command = containerResource.Entrypoint;
        }
        spec.PemCertificates = pemCertificates;

        // Configure the terminal spec if the resource has a TerminalAnnotation.
        // Containers are always single-replica, so we use the host at index 0
        // (TerminalAnnotation always has at least one entry). PTY allocation
        // is implemented by DCP for Windows (ConPTY), Linux, and macOS
        // (Unix98 /dev/ptmx); the container runtime CLI's `attach` command
        // is what actually gets PTY-attached, so behaviour is uniform across
        // hosts that support docker/podman.
        if (modelContainer.TryGetAnnotationsOfType<TerminalAnnotation>(out var terminalAnnotations))
        {
            var terminalAnnotation = terminalAnnotations.FirstOrDefault();
            if (terminalAnnotation is not null)
            {
                if (terminalAnnotation.TerminalHosts.Count > 0)
                {
                    spec.Terminal = new TerminalSpec
                    {
                        UdsPath = terminalAnnotation.TerminalHosts[0].Layout.ProducerUdsPath,
                        // The Aspire terminal host owns the listener at UdsPath; DCP must dial it.
                        SocketMode = "connect",
                        Cols = terminalAnnotation.Options.Columns,
                        Rows = terminalAnnotation.Options.Rows
                    };
                }
                else
                {
                    logger.LogWarning(
                        "Could not determine a producer UDS path for container resource '{ResourceName}'; terminal will not be attached.",
                        modelContainer.Name);
                }
            }
        }

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
            if (_model.Resources.TryGetByName(KnownResourceNames.AspireDashboard, out var dashboardResource)
                && HostResourceWithEndpoints.Create(dashboardResource) is HostResourceWithEndpoints dashboard)
            {
                hostDependencies.Add(dashboard);
            }
        }

        return hostDependencies;
    }

    internal async Task CreateHostDependentContainerAsync(RenderedModelResource<Container> cr, ImmutableArray<HostResourceWithEndpoints> hostDependencies, ContainerNetworkEndpointContext context, IDcpObjectFactory factory, CancellationToken cToken)
    {
        cToken.ThrowIfCancellationRequested();

        await _containerNetworkEndpointProvisioner
            .EnsureEndpointsAsync(hostDependencies, context, factory, cToken)
            .ConfigureAwait(false);

        var hostEndpointAllocatedTasks = hostDependencies
            .SelectMany(h => h.Endpoints)
            .Where(endpoint => endpoint.Protocol == ProtocolType.Tcp)
            .Select(e => e.AllAllocatedEndpoints.GetAllocatedEndpointAsync(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, cToken))
            .ToArray();
        await Task.WhenAll(hostEndpointAllocatedTasks).ConfigureAwait(false);

        await BuildAndCreateContainerAsync(cr, _loggerService.GetLogger(cr.ModelResource), factory, cToken).ConfigureAwait(false);
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
                CertificateWithKeyPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.pem"),
                PfxPath = ReferenceExpression.Create($"{serverAuthCertificatesBasePath}/{cert.Thumbprint}.pfx"),
            })
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
                CertificateWithKeyPath = tlsCertificateConfiguration.CertificateWithKeyPathReference,
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
                certificate: tlsCertificateConfiguration.Certificate,
                password: tlsCertificateConfiguration.Password,
                needKeyPem: tlsCertificateConfiguration.IsKeyPathReferenced || tlsCertificateConfiguration.IsCertificateWithKeyPathReferenced,
                needPfx: tlsCertificateConfiguration.IsPfxPathReferenced,
                cancellationToken
            ).ConfigureAwait(false);

            var certificateFiles = new List<ContainerFileSystemEntry>()
            {
                new ContainerFileSystemEntry
                {
                    Name = thumbprint + ".crt",
                    Type = ContainerFileSystemEntryType.File,
                    Contents = new string(publicCertificatePem),
                },
            };

            if (keyPem is not null)
            {
                certificateFiles.Add(new ContainerFileSystemEntry
                {
                    Name = thumbprint + ".key",
                    Type = ContainerFileSystemEntryType.File,
                    Contents = new string(keyPem),
                });

                if (tlsCertificateConfiguration.IsCertificateWithKeyPathReferenced)
                {
                    certificateFiles.Add(new ContainerFileSystemEntry
                    {
                        Name = thumbprint + ".pem",
                        Type = ContainerFileSystemEntryType.File,
                        Contents = new string([.. keyPem, '\n', .. publicCertificatePem]),
                    });
                }

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
                        Services = _executionContext.Services,
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

    private static async Task ApplyBuildArgumentsAsync(Container dcpContainerResource, IResource modelContainerResource, DistributedApplicationExecutionContext executionContext, ILogger logger, CancellationToken cancellationToken)
    {
        if (modelContainerResource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault() is { } dockerfileBuildAnnotation)
        {
            await DockerfileHelper.ExecuteDockerfileFactoryAsync(dockerfileBuildAnnotation, modelContainerResource, executionContext.Services, cancellationToken).ConfigureAwait(false);

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

#pragma warning disable ASPIREPIPELINES003 // ContainerBuildOptions APIs are experimental.
            var buildOptionsContext = await modelContainerResource.ProcessContainerBuildOptionsCallbackAsync(
                executionContext.Services,
                logger,
                executionContext,
                cancellationToken).ConfigureAwait(false);

            if (buildOptionsContext.TargetPlatform is { } targetPlatform)
            {
                dcpContainerResource.Spec.Build.Platform = ToDcpPlatformString(targetPlatform);
            }
#pragma warning restore ASPIREPIPELINES003
        }
    }

    // Maps the publishing-side ContainerTargetPlatform enum to DCP-native ContainerPlatform string
    // constants. The publishing type is fully qualified so the DCP layer doesn't carry a
    // `using Aspire.Hosting.Publishing` directive.
#pragma warning disable ASPIREPIPELINES003 // ContainerTargetPlatform is experimental.
    private static string ToDcpPlatformString(Publishing.ContainerTargetPlatform platform)
    {
        var parts = new List<string>();
        if (platform.HasFlag(Publishing.ContainerTargetPlatform.LinuxAmd64)) { parts.Add(ContainerPlatform.LinuxAmd64); }
        if (platform.HasFlag(Publishing.ContainerTargetPlatform.LinuxArm64)) { parts.Add(ContainerPlatform.LinuxArm64); }
        if (platform.HasFlag(Publishing.ContainerTargetPlatform.LinuxArm)) { parts.Add(ContainerPlatform.LinuxArm); }
        if (platform.HasFlag(Publishing.ContainerTargetPlatform.Linux386)) { parts.Add(ContainerPlatform.Linux386); }
        if (platform.HasFlag(Publishing.ContainerTargetPlatform.WindowsAmd64)) { parts.Add(ContainerPlatform.WindowsAmd64); }
        if (platform.HasFlag(Publishing.ContainerTargetPlatform.WindowsArm64)) { parts.Add(ContainerPlatform.WindowsArm64); }

        if (parts.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown container target platform");
        }

        return string.Join(",", parts);
    }
#pragma warning restore ASPIREPIPELINES003

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

            if (!ea.IsProxied && ea.SpecifiedPort is int hostPort)
            {
                sp.Service.Spec.Port ??= hostPort;
                portSpec.HostPort = hostPort;
            }

            switch (ea.Protocol)
            {
                case ProtocolType.Tcp:
                    portSpec.Protocol = PortProtocol.TCP;
                    break;
                case ProtocolType.Udp:
                    portSpec.Protocol = PortProtocol.UDP;
                    break;
            }

            if (ea.TargetHost != KnownHostNames.Localhost)
            {
                portSpec.HostIP = ea.TargetHost;
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
