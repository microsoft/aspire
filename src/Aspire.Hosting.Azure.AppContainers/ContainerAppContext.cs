// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

internal sealed class ContainerAppContext(IResource resource, ContainerAppEnvironmentContext containerAppEnvironmentContext)
    : BaseContainerAppContext(resource, containerAppEnvironmentContext)
{
    // Endpoint state after processing
    private (int? Port, bool Http2, bool External)? _httpIngress;
    private readonly List<int> _additionalPorts = [];

    public override void BuildContainerApp(AzureResourceInfrastructure infra)
    {
        _infrastructure = infra;
        // Write a fake parameter for the container app environment
        // so azd knows the Dashboard URL - see https://github.com/microsoft/aspire/issues/8449.
        // This is temporary until a real fix can be made in azd.
        AllocateParameter(_containerAppEnvironmentContext.Environment.ContainerAppDomain);

        var containerAppIdParam = AllocateParameter(_containerAppEnvironmentContext.Environment.ContainerAppEnvironmentId);

        ProvisioningParameter? containerImageParam = null;

        if (!TryGetContainerImageName(Resource, out var containerImageName))
        {
            AllocateContainerRegistryParameters();

            containerImageParam = AllocateContainerImageParameter();
        }

        var containerAppResource = CreateContainerApp();

        BicepValue<string>? containerAppIdentityId = null;

        if (Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentityAnnotation))
        {
            var appIdentityResource = appIdentityAnnotation.IdentityResource;

            containerAppIdentityId = appIdentityResource.Id.AsProvisioningParameter(infra);

            var id = BicepFunction.Interpolate($"{containerAppIdentityId}").Compile().ToString();

            containerAppResource.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned;
            containerAppResource.Identity.UserAssignedIdentities[id] = new UserAssignedIdentityDetails();
        }

        AddContainerRegistryManagedIdentity(containerAppResource.Identity);

        containerAppResource.EnvironmentId = containerAppIdParam;

        var configuration = containerAppResource.Configuration;

        AddIngress(configuration);

        AddContainerRegistryParameters(reg => configuration.Registries = reg);

        var template = new ContainerAppTemplate();
        containerAppResource.Template = template;

        template.Scale = new ContainerAppScale()
        {
            MinReplicas = Resource.GetReplicaCount()
        };

        var containerAppContainer = new ContainerAppContainer();
        template.Containers = [containerAppContainer];

        containerAppContainer.Image = containerImageParam is null ? containerImageName! : containerImageParam;
        containerAppContainer.Name = NormalizedContainerAppName;

        SetEntryPoint(containerAppContainer);
        AddEnvironmentVariablesAndCommandLineArgs(
            containerAppContainer,
            () => configuration.Secrets ??= [],
            containerAppIdentityId);
        AddAzureClientId(appIdentityAnnotation?.IdentityResource, containerAppContainer.Env);
        AddVolumes(template.Volumes, containerAppContainer);
        AddProbes(containerAppContainer);

        infra.Add(containerAppResource);

        if (Resource.TryGetAnnotationsOfType<AzureContainerAppCustomizationAnnotation>(out var annotations))
        {
            foreach (var a in annotations)
            {
                a.Configure(infra, containerAppResource);
            }
        }
    }

    private ContainerApp CreateContainerApp()
    {
        var containerApp = new ContainerApp(Infrastructure.NormalizeBicepIdentifier(Resource.Name))
        {
            Name = NormalizedContainerAppName
        };

        var configuration = new ContainerAppConfiguration()
        {
            ActiveRevisionsMode = ContainerAppActiveRevisionsMode.Single,
        };
        containerApp.Configuration = configuration;

        const string latestPreview = "2025-02-02-preview"; // these properties are currently only available in preview

        // default autoConfigureDataProtection to true for .NET projects
        if (Resource is ProjectResource)
        {
            containerApp.ResourceVersion = latestPreview;

            var value = new BicepValue<bool>(true);
            ((IBicepValue)value).Self = new BicepValueReference(configuration, "AutoConfigureDataProtection", ["runtime", "dotnet", "autoConfigureDataProtection"]);
            configuration.ProvisionableProperties["AutoConfigureDataProtection"] = value;
        }

        // default kind to functionapp for Azure Functions
        if (Resource.HasAnnotationOfType<AzureFunctionsAnnotation>())
        {
            containerApp.ResourceVersion = latestPreview;

            var value = new BicepValue<string>("functionapp");
            ((IBicepValue)value).Self = new BicepValueReference(containerApp, "Kind", ["kind"]);
            containerApp.ProvisionableProperties["Kind"] = value;
        }

        return containerApp;
    }

    protected override void ProcessEndpoints()
    {
        // Resolve endpoint ports using the centralized helper
        var resolvedEndpoints = Resource.ResolveEndpoints();

        if (resolvedEndpoints.Count == 0)
        {
            return;
        }

        // Validate transport layer: only http-based and tcp transports are supported by Container Apps.
        // The URI scheme (e.g. "redis", "rediss", "foo") is independent of transport.
        var unsupportedEndpoints = resolvedEndpoints.Where(r => r.Endpoint.Transport is not ("http" or "http2" or "tcp")).ToArray();

        if (unsupportedEndpoints.Length > 0)
        {
            throw new NotSupportedException($"The endpoint(s) {string.Join(", ", unsupportedEndpoints.Select(r => $"'{r.Endpoint.Name}'"))} specify an unsupported transport. The supported transports are 'http', 'http2', and 'tcp'.");
        }

        // Group resolved endpoints by target port (aka destinations), this gives us the logical bindings or destinations
        var endpointsByTargetPort = resolvedEndpoints
            .Select((resolved, index) => (resolved, index))
            .GroupBy(x => x.resolved.TargetPort.Value)
            .Select(g => new
            {
                Port = g.Key,
                ResolvedEndpoints = g.Select(x => x.resolved).ToArray(),
                External = g.Any(x => x.resolved.Endpoint.IsExternal),
                IsHttpOnly = g.All(x => x.resolved.Endpoint.Transport is "http" or "http2"),
                AnyH2 = g.Any(x => x.resolved.Endpoint.Transport is "http2"),
                UniqueTransports = g.Select(x => x.resolved.Endpoint.Transport).Distinct().ToArray(),
                Index = g.Min(x => x.index)
            })
            .ToList();

        // Failure cases

        // Multiple external endpoints are not supported
        if (endpointsByTargetPort.Count(g => g.External) > 1)
        {
            throw new NotSupportedException("Multiple external endpoints are not supported");
        }

        // Any external non-http endpoints are not supported
        if (endpointsByTargetPort.Any(g => g.External && !g.IsHttpOnly))
        {
            throw new NotSupportedException("External non-HTTP(s) endpoints are not supported");
        }

        // Don't allow mixing http and tcp transports on the same target port
        static bool Compatible(string[] transports) =>
            transports.All(t => t is "http" or "http2") || transports.All(t => t is "tcp");

        if (endpointsByTargetPort.Any(g => !Compatible(g.UniqueTransports)))
        {
            throw new NotSupportedException("HTTP(s) and TCP endpoints cannot be mixed");
        }

        // Get all http only groups
        var httpOnlyEndpoints = endpointsByTargetPort.Where(g => g.IsHttpOnly).OrderBy(g => g.Index).ToArray();

        // Do we only have one?
        var httpIngress = httpOnlyEndpoints.Length == 1 ? httpOnlyEndpoints[0] : null;

        if (httpIngress is null)
        {
            // We have more than one, pick prefer external one
            var externalHttp = httpOnlyEndpoints.Where(g => g.External).ToArray();

            if (externalHttp.Length == 1)
            {
                httpIngress = externalHttp[0];
            }
            else if (httpOnlyEndpoints.Length > 0)
            {
                httpIngress = httpOnlyEndpoints[0];
            }
        }

        if (httpIngress is not null)
        {
            // We're processed the http ingress, remove it from the list
            endpointsByTargetPort.Remove(httpIngress);

            var targetPort = httpIngress.Port ?? (Resource is ProjectResource ? null : 80);

            _httpIngress = (targetPort, httpIngress.AnyH2, httpIngress.External);

            foreach (var resolved in httpIngress.ResolvedEndpoints)
            {
                var endpoint = resolved.Endpoint;
                var preserveHttp = _containerAppEnvironmentContext.Environment.PreserveHttpEndpoints;

                // By default, HTTP ingress uses HTTPS in ACA (HTTP→HTTPS redirect breaks WebSocket upgrades)
                // If PreserveHttpEndpoints is true, keep the original scheme
                var scheme = preserveHttp ? endpoint.UriScheme : "https";
                var port = scheme is "http" ? 80 : 443;

                _endpointMapping[endpoint.Name] = new(scheme, NormalizedContainerAppName, port, targetPort, true, httpIngress.External, endpoint.TlsEnabled);
            }

            // Record HTTP endpoints being upgraded (logged once at environment level)
            if (!_containerAppEnvironmentContext.Environment.PreserveHttpEndpoints)
            {
                var upgradedEndpoints = httpIngress.ResolvedEndpoints
                    .Where(r => r.Endpoint.UriScheme is "http")
                    .Select(r => r.Endpoint.Name)
                    .ToArray();

                _containerAppEnvironmentContext.RecordHttpsUpgrade(Resource.Name, upgradedEndpoints);
            }
        }

        if (endpointsByTargetPort.Count > 5)
        {
            _containerAppEnvironmentContext.Logger.LogWarning("More than 5 additional ports are not supported. See https://learn.microsoft.com/azure/container-apps/ingress-overview#tcp for more details.");
        }

        foreach (var g in endpointsByTargetPort)
        {
            if (g.Port is null)
            {
                throw new NotSupportedException("Container port is required for all endpoints");
            }

            _additionalPorts.Add(g.Port.Value);

            foreach (var resolved in g.ResolvedEndpoints)
            {
                var endpoint = resolved.Endpoint;
                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, NormalizedContainerAppName, resolved.ExposedPort.Value ?? g.Port.Value, g.Port.Value, false, g.External, endpoint.TlsEnabled);
            }
        }
    }

    private void AddIngress(ContainerAppConfiguration config)
    {
        if (_httpIngress is null && _additionalPorts.Count == 0)
        {
            return;
        }

        // Now we map the remaining endpoints. These should be internal only tcp/http based endpoints
        var skipAdditionalPort = 0;

        var caIngress = new ContainerAppIngressConfiguration();

        if (_httpIngress is { } ingress)
        {
            caIngress.External = ingress.External;
            caIngress.TargetPort = ingress.Port ?? AsInt(AllocateContainerPortParameter());
            caIngress.Transport = ingress.Http2 ? ContainerAppIngressTransportMethod.Http2 : ContainerAppIngressTransportMethod.Http;
        }
        else if (_additionalPorts.Count > 0)
        {
            // First port is the default
            var port = _additionalPorts[0];

            skipAdditionalPort++;

            caIngress.External = false;
            caIngress.TargetPort = port;
            caIngress.Transport = ContainerAppIngressTransportMethod.Tcp;
        }

        // Add additional ports
        // https://learn.microsoft.com/azure/container-apps/ingress-how-to?pivots=azure-cli#use-additional-tcp-ports
        var additionalPorts = _additionalPorts.Skip(skipAdditionalPort);
        if (additionalPorts.Any())
        {
            foreach (var port in additionalPorts)
            {
                caIngress.AdditionalPortMappings.Add(new IngressPortMapping
                {
                    External = false,
                    TargetPort = port
                });
            }
        }

        config.Ingress = caIngress;
    }
}
