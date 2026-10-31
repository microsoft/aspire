// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Kubernetes;

internal sealed class KubernetesEnvironmentContext(KubernetesEnvironmentResource environment, ILogger logger)
{
    private readonly Dictionary<IResource, KubernetesResource> _kubernetesComponents = new(new ResourceNameComparer());
    private readonly Dictionary<string, ParameterResource> _endpointInputs = new(StringComparer.OrdinalIgnoreCase);

    public ParameterResource GetEndpointInput(EndpointReferenceExpression reference, DistributedApplicationExecutionContext executionContext)
    {
        if (!_endpointInputs.TryGetValue(reference.ValueExpression, out var parameter))
        {
            var name = ResourceNameHelpers.GetEndpointPropertyInputName(
                reference.Endpoint.Resource.Name, reference.Endpoint.EndpointAnnotation.Name, reference.Property.ToString(), 64);
            var resources = executionContext.Services.GetRequiredService<DistributedApplicationModel>().Resources;
            parameter = ParameterResourceBuilderExtensions.CreateParameter(
                executionContext.Services.GetRequiredService<IConfiguration>(), name, secret: false, GetOriginDefault(reference, resources));
            _endpointInputs.Add(reference.ValueExpression, parameter);
        }

        return parameter;
    }

    private string? GetOriginDefault(EndpointReferenceExpression reference, IEnumerable<IResource> resources)
    {
        var origins = new List<string>();
        foreach (var ingress in resources.OfType<KubernetesIngressResource>().Where(ingress => ingress.Parent == environment && ingress.ShouldMaterialize))
        {
            foreach (var path in ingress.Paths.Where(path => Matches(path.Endpoint)))
            {
                if (!AddOrigins(path.Host, ingress.Hostnames, ingress.TlsConfigs.Count > 0, gateway: false))
                {
                    return null;
                }
            }
        }

        foreach (var gateway in resources.OfType<KubernetesGatewayResource>().Where(gateway => gateway.Parent == environment && gateway.ShouldMaterialize))
        {
            foreach (var route in gateway.Routes.Where(route => Matches(route.Endpoint)))
            {
                if (!AddOrigins(route.Host, gateway.Hostnames, gateway.TlsConfigs.Count > 0, gateway: true))
                {
                    return null;
                }
            }
        }

        var distinct = origins.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 0 ? null : reference.Property switch
        {
            EndpointProperty.BrowserOrigin => distinct[0],
            EndpointProperty.BrowserOriginsJson => System.Text.Json.JsonSerializer.Serialize(distinct),
            _ => string.Join(",", distinct)
        };

        bool Matches(EndpointReference endpoint) =>
            string.Equals(endpoint.Resource.Name, reference.Endpoint.Resource.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(endpoint.EndpointName, reference.Endpoint.EndpointName, StringComparison.OrdinalIgnoreCase);

        bool AddOrigins(string? routeHost, IReadOnlyList<ReferenceExpression> hostnames, bool tls, bool gateway)
        {
            // Host-scoped routes override inherited hostnames. Wildcard/hostless routes and
            // deferred host expressions cannot establish a concrete published default.
            IReadOnlyList<ReferenceExpression> hosts = routeHost is not null ? [ReferenceExpression.Create($"{routeHost}")] : hostnames;
            if (hosts.Count == 0)
            {
                return false;
            }

            foreach (var host in hosts)
            {
                // A host-scoped route can differ from the hostnames on the TLS listener.
                // Use HTTPS only when that listener also serves the selected hostname.
                var usesTls = tls && (routeHost is null || hostnames.Count == 0 ||
                    hostnames.Any(hostname => hostname.ValueProviders.Count == 0 &&
                        MatchesHostname(hostname.ValueExpression, routeHost, gateway)));
                if (tls && !usesTls && hostnames.Any(hostname => hostname.ValueProviders.Count != 0))
                {
                    return false;
                }
                if (host.ValueProviders.Count != 0 ||
                    Uri.CheckHostName(host.ValueExpression) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
                    !Uri.TryCreate($"{(usesTls ? "https" : "http")}://{host.ValueExpression}", UriKind.Absolute, out var uri))
                {
                    return false;
                }

                origins.Add(new UriBuilder(uri) { Host = uri.IdnHost }.Uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
            }

            return true;
        }
    }

    private static bool MatchesHostname(string pattern, string hostname, bool gateway)
    {
        if (!HostString.MatchesAny(hostname, [pattern]))
        {
            return false;
        }

        // HostString supports Gateway-style multi-label wildcard matching. Ingress
        // additionally restricts "*.example.com" to a single label before the suffix.
        // https://kubernetes.io/docs/concepts/services-networking/ingress/#hostname-wildcards
        if (gateway || !pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return true;
        }

        var prefixLength = hostname.Length - pattern.Length + 1;
        return !hostname.AsSpan(0, prefixLength).Contains('.');
    }

    public ILogger Logger => logger;

    public async Task<KubernetesResource> CreateKubernetesResourceAsync(IResource resource, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (_kubernetesComponents.TryGetValue(resource, out var existingResource))
        {
            return existingResource;
        }

        logger.LogInformation("Creating Kubernetes resource for {ResourceName}", resource.Name);

        var serviceResource = new KubernetesResource(resource.Name, resource, environment);
        _kubernetesComponents[resource] = serviceResource;

        await serviceResource.ProcessResourceAsync(this, executionContext, cancellationToken).ConfigureAwait(false);

        return serviceResource;
    }
}
