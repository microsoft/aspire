// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // AzureSubnetResource is evaluation-only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting;

/// <summary>
/// Provides the "pit of success" extension method <c>WithClusterDefaults</c>, which collapses
/// the verbose ~15-line AKS + AGC + cert-manager + VNet + Gateway recipe down to a single call.
/// </summary>
public static class AzureKubernetesClusterDefaultsExtensions
{
    /// <summary>
    /// Configures the AKS environment with a complete production-grade default topology in
    /// one call: a VNet with delegated subnets, a system node pool, an AGC public load balancer,
    /// cert-manager with a Let's Encrypt <c>ClusterIssuer</c>, a TLS-enabled <c>Gateway</c>
    /// attached to that load balancer, and auto-routing of every external HTTP endpoint in the
    /// application model to that gateway.
    /// </summary>
    /// <param name="builder">The Azure Kubernetes environment resource builder.</param>
    /// <param name="acmeEmail">
    /// Parameter resource carrying the contact email registered with the Let's Encrypt ACME
    /// account. Required because Let's Encrypt mandates an account email and surfacing it as
    /// a parameter keeps it out of source control.
    /// </param>
    /// <param name="configure">Optional callback to tune <see cref="ClusterDefaultsOptions"/>.</param>
    /// <returns>The <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method exists because the manual recipe (visible in <c>playground/CertManagerDemo</c>)
    /// is verbose and full of footguns: choosing CIDR ranges that don't collide with the AKS
    /// service CIDR, delegating the right subnet to <c>Microsoft.ServiceNetworking/trafficControllers</c>,
    /// remembering to set <c>WithSystemNodePool</c>, attaching cert-manager to the gateway via the
    /// right annotation, and so on. <c>WithClusterDefaults</c> bakes in the choices that work for
    /// ~80% of users while leaving every individual piece overridable via
    /// <see cref="ClusterDefaultsOptions"/>.
    /// </para>
    /// <para>
    /// Auto-routing runs at <see cref="BeforePublishEvent"/> time so user-authored
    /// <c>WithRoute</c> calls always win — a resource that the user has explicitly
    /// routed is skipped. The
    /// auto-router walks the application model, ignores infrastructure resources (gateway,
    /// load balancer, cert-manager, issuer, vnet, subnet, dashboard, AKS env), and for each
    /// remaining resource with one or more <c>IsExternal == true</c> HTTP endpoints adds a
    /// route under <see cref="ClusterDefaultsOptions.RoutePathTemplate"/>.
    /// </para>
    /// <para>
    /// The defaults install Let's Encrypt production. For development loops that redeploy
    /// frequently, set <see cref="ClusterDefaultsOptions.AcmeEnvironment"/> to
    /// <see cref="LetsEncryptEnvironment.Staging"/> to avoid burning the ≈5 certs/hostname/week
    /// production rate limit.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var acmeEmail = builder.AddParameter("acme-email");
    ///
    /// var aks = builder.AddAzureKubernetesEnvironment("aks")
    ///                  .WithClusterDefaults(acmeEmail);
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///        .WithExternalHttpEndpoints();
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Configures the AKS environment with a complete VNet + AGC + cert-manager + TLS-enabled gateway in one call, and auto-routes every external HTTP endpoint", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithClusterDefaults(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<ParameterResource> acmeEmail,
        Action<ClusterDefaultsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(acmeEmail);

        // Materialize options up-front. Snapshot-style so later mutation of the options
        // bag (which we don't expose, but the callback could capture and re-invoke) cannot
        // drift the resources we register below.
        var options = new ClusterDefaultsOptions();
        configure?.Invoke(options);

        var appBuilder = builder.ApplicationBuilder;
        var aksName = builder.Resource.Name;

        // 1. VNet + AKS-node + ALB subnets. Names follow the playground/CertManagerDemo
        //    convention so the diff between the verbose and one-line recipes is obvious.
        var vnet = appBuilder.AddAzureVirtualNetwork($"{aksName}-vnet", options.AddressSpace);
        var aksSubnet = vnet.AddSubnet("aks-nodes", options.AksSubnetCidr);
        var albSubnet = vnet.AddSubnet("alb-public", options.LoadBalancerSubnetCidr);

        // 2. Wire the AKS env to the node subnet and configure the system and (by default)
        //    user node pools. VM sizes can be overridden by ParameterResource so callers
        //    can swap SKUs at deploy time without editing the AppHost — common when a
        //    region runs out of quota for the default SKU. The underlying node-pool config
        //    takes a plain string, so we resolve the parameter synchronously here. That's
        //    fine because parameter values are sourced from configuration/env at AppHost
        //    startup and don't depend on any other resource being up.
        var systemVmSize = ResolveVmSize(options.SystemNodePoolVmSizeParameter, options.SystemNodePoolVmSize);

        builder.WithSubnet(aksSubnet)
               .WithSystemNodePool(
                   systemVmSize,
                   minCount: options.SystemNodePoolMinCount,
                   maxCount: options.SystemNodePoolMaxCount);

        if (options.IncludeUserNodePool)
        {
            var userVmSize = ResolveVmSize(options.UserNodePoolVmSizeParameter, options.UserNodePoolVmSize);

            builder.AddNodePool(
                options.UserNodePoolName,
                userVmSize,
                minCount: options.UserNodePoolMinCount,
                maxCount: options.UserNodePoolMaxCount);
        }

        // 3. Public AGC load balancer. AddLoadBalancer handles the AGC subnet delegation
        //    to Microsoft.ServiceNetworking/trafficControllers idempotently, including the
        //    last-write-wins safety net described in its xmldoc.
        var loadBalancer = builder.AddLoadBalancer(options.LoadBalancerName, albSubnet);

        // 4. Gateway. We need a reference for the auto-router below. Always create it so
        //    callers can WithRoute(...) onto it even when EnableTls is false.
        var gateway = builder.AddGateway(options.GatewayName)
                             .WithLoadBalancer(loadBalancer);

        // 5. Optional TLS chain: cert-manager + Let's Encrypt + HTTPS listener on the gateway.
        if (options.EnableTls)
        {
            var certManager = builder.AddCertManager(options.CertManagerName);

            var issuerBuilder = certManager.AddIssuer(options.IssuerName);
            issuerBuilder = options.AcmeEnvironment switch
            {
                Azure.Kubernetes.LetsEncryptEnvironment.Staging => issuerBuilder.WithLetsEncryptStaging(acmeEmail),
                Azure.Kubernetes.LetsEncryptEnvironment.Production => issuerBuilder.WithLetsEncryptProduction(acmeEmail),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(configure),
                    options.AcmeEnvironment,
                    $"Unknown {nameof(LetsEncryptEnvironment)} value."),
            };
            issuerBuilder.WithHttp01Solver();

            // WithTls(issuer, configure) hands control of HTTPS termination, the
            // HTTP→HTTPS redirect, and HSTS to the gateway in one call. See
            // microsoft/aspire#17158 for the rationale behind that consolidation.
            gateway.WithTls(issuerBuilder, options.ConfigureTls);
        }

        // 6. Auto-route external HTTP endpoints at publish time. Defer to BeforePublishEvent
        //    so user-authored WithRoute(...) calls have already mutated the gateway routes
        //    (they happen synchronously during AppHost construction). We skip any resource
        //    the user has already wired up, and any infrastructure resources we created.
        if (options.AutoRouteExternalEndpoints)
        {
            // Capture the names we created so the auto-router can detect "this is one of
            // ours, leave it alone". Capturing by name (not by reference) is intentional
            // so that downstream resource swaps stay correct.
            var infraNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                aksName,
                vnet.Resource.Name,
                aksSubnet.Resource.Name,
                albSubnet.Resource.Name,
                loadBalancer.Resource.Name,
                gateway.Resource.Name,
            };

            if (options.EnableTls)
            {
                infraNames.Add(options.CertManagerName);
                infraNames.Add(options.IssuerName);
            }

            var pathTemplate = options.RoutePathTemplate;
            var capturedGateway = gateway;

            appBuilder.Eventing.Subscribe<BeforePublishEvent>((evt, _) =>
            {
                AutoRouteExternalEndpoints(evt.Model, capturedGateway, infraNames, pathTemplate);
                return Task.CompletedTask;
            });
        }

        return builder;
    }

    /// <summary>
    /// Walks the application model and attaches every external HTTP endpoint to the
    /// gateway under the configured path template. Skips infrastructure we created and
    /// any resource that the user has already routed (user-wins).
    /// </summary>
    private static void AutoRouteExternalEndpoints(
        DistributedApplicationModel model,
        IResourceBuilder<KubernetesGatewayResource> gatewayBuilder,
        HashSet<string> infraNames,
        string pathTemplate)
    {
        // Snapshot the resource names already routed on the gateway. Routes is an internal
        // List<GatewayRouteConfig> on KubernetesGatewayResource — we access it via
        // InternalsVisibleTo set in src/Aspire.Hosting.Kubernetes/*.csproj. Snapshotting by
        // name (not reference) is intentional: it survives any resource-swap/bait-and-switch
        // applied later in the pipeline.
        var alreadyRoutedResources = new HashSet<string>(
            gatewayBuilder.Resource.Routes.Select(r => r.Endpoint.Resource.Name),
            StringComparer.OrdinalIgnoreCase);

        // Snapshot the already-used paths so we don't collide with a user route either.
        var usedPaths = new HashSet<string>(
            gatewayBuilder.Resource.Routes.Select(r => r.Path),
            StringComparer.Ordinal);

        var groupedByResource = model.Resources
            .OfType<IResourceWithEndpoints>()
            .Where(r => !infraNames.Contains(r.Name))
            .Where(r => !alreadyRoutedResources.Contains(r.Name))
            .Where(r => !IsInfrastructureResource(r))
            .Select(r => new
            {
                Resource = r,
                ExternalHttpEndpoints = r.Annotations
                    .OfType<EndpointAnnotation>()
                    .Where(e => e.IsExternal && IsHttpScheme(e.UriScheme))
                    .ToList(),
            })
            .Where(x => x.ExternalHttpEndpoints.Count > 0)
            .ToList();

        foreach (var entry in groupedByResource)
        {
            // Multi-endpoint disambiguation: when a resource has >1 external HTTP
            // endpoint, append the endpoint name to the template so each one gets a
            // distinct path.
            var multipleEndpoints = entry.ExternalHttpEndpoints.Count > 1;

            foreach (var endpoint in entry.ExternalHttpEndpoints)
            {
                var path = pathTemplate.Replace("{name}", entry.Resource.Name, StringComparison.Ordinal);
                if (multipleEndpoints)
                {
                    path = $"{path}-{endpoint.Name}";
                }

                if (!usedPaths.Add(path))
                {
                    // Path collision (either against a user route or another auto route).
                    // Skip rather than silently overwrite — that's the footgun this method
                    // is designed to avoid.
                    continue;
                }

                var endpointRef = new EndpointReference(entry.Resource, endpoint.Name);
                gatewayBuilder.WithRoute(path, endpointRef);
            }
        }
    }

    /// <summary>
    /// Conservative infrastructure-resource filter. Catches the well-known Kubernetes
    /// hosting infrastructure types by full name so we don't take a hard reference to
    /// every type (some live in adjacent assemblies that may not be loaded). The string
    /// comparison is exact on type full name; subclasses are excluded on purpose.
    /// </summary>
    private static bool IsInfrastructureResource(IResource resource)
    {
        var fullName = resource.GetType().FullName;
        return fullName switch
        {
            "Aspire.Hosting.Kubernetes.KubernetesEnvironmentResource" => true,
            "Aspire.Hosting.Azure.Kubernetes.AzureKubernetesEnvironmentResource" => true,
            "Aspire.Hosting.Azure.Kubernetes.AzureKubernetesLoadBalancerResource" => true,
            "Aspire.Hosting.Azure.AzureVirtualNetworkResource" => true,
            "Aspire.Hosting.Azure.AzureSubnetResource" => true,
            "Aspire.Hosting.Azure.AzureContainerRegistryResource" => true,
            "Aspire.Hosting.Kubernetes.KubernetesGatewayResource" => true,
            "Aspire.Hosting.Kubernetes.KubernetesIngressResource" => true,
            "Aspire.Hosting.Kubernetes.KubernetesHelmChartResource" => true,
            "Aspire.Hosting.Kubernetes.CertManagerResource" => true,
            "Aspire.Hosting.Kubernetes.CertManagerIssuerResource" => true,
            _ => false,
        };
    }

    /// <summary>
    /// True when the endpoint's URI scheme is one we'd want to attach to an HTTPRoute.
    /// HTTPS endpoints are included because the gateway terminates TLS in front of them
    /// — the route still uses the cluster-internal scheme to talk to the backend.
    /// </summary>
    /// <summary>
    /// True when the endpoint's URI scheme is one we'd want to attach to an HTTPRoute.
    /// HTTPS endpoints are included because the gateway terminates TLS in front of them
    /// — the route still uses the cluster-internal scheme to talk to the backend.
    /// </summary>
    private static bool IsHttpScheme(string uriScheme)
        => string.Equals(uriScheme, "http", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uriScheme, "https", StringComparison.OrdinalIgnoreCase);

    // The underlying AKS node-pool API takes a plain string for VM size, so when the
    // caller supplies a ParameterResource override we resolve it synchronously here.
    // ParameterResource sources its value from configuration/env at AppHost startup and
    // doesn't depend on any other resource, so the blocking GetAwaiter().GetResult() is
    // safe in this context (we're running on the AppHost build thread, not in a hot path).
    private static string ResolveVmSize(IResourceBuilder<ParameterResource>? parameterOverride, string fallback)
    {
        if (parameterOverride is null)
        {
            return fallback;
        }

        var value = parameterOverride.Resource.GetValueAsync(default).AsTask().GetAwaiter().GetResult();
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
