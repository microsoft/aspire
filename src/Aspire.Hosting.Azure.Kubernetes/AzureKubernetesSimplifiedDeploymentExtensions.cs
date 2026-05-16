// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // AzureSubnetResource is evaluation-only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides the "pit of success" extension method <c>WithSimplifiedDeployment</c>, which collapses
/// the verbose ~15-line AKS + AGC + cert-manager + VNet + Gateway recipe down to a single call.
/// </summary>
public static class AzureKubernetesSimplifiedDeploymentExtensions
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
    /// <param name="configure">Optional callback to tune <see cref="SimplifiedDeploymentOptions"/>.</param>
    /// <returns>The <see cref="IResourceBuilder{AzureKubernetesEnvironmentResource}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method exists because the manual recipe (visible in <c>playground/CertManagerDemo</c>)
    /// is verbose and full of footguns: choosing CIDR ranges that don't collide with the AKS
    /// service CIDR, delegating the right subnet to <c>Microsoft.ServiceNetworking/trafficControllers</c>,
    /// remembering to set <c>WithSystemNodePool</c>, attaching cert-manager to the gateway via the
    /// right annotation, and so on. <c>WithSimplifiedDeployment</c> bakes in the choices that work for
    /// ~80% of users while leaving every individual piece overridable via
    /// <see cref="SimplifiedDeploymentOptions"/>.
    /// </para>
    /// <para>
    /// Auto-routing runs as part of the Kubernetes <c>prepare-deployment-targets</c>
    /// pipeline step so user-authored <c>WithRoute</c> calls always win — a resource
    /// that the user has explicitly routed is skipped. The auto-router walks the
    /// application model, ignores infrastructure resources (gateway, load balancer,
    /// cert-manager, issuer, vnet, subnet, dashboard, AKS env), and mounts the single
    /// remaining resource that has an external HTTP endpoint at the gateway root
    /// (<c>"/"</c>). If more than one such resource is present the auto-router
    /// throws — see the remarks on the single-frontend constraint below.
    /// </para>
    /// <para>
    /// <b>Single-frontend constraint:</b> the simplified path supports exactly one
    /// resource with external HTTP endpoints. Multi-frontend hostname allocation
    /// requires stable endpoint-to-listener mappings across deploys and bumps against
    /// AGC's per-load-balancer frontend cap, so applications that need more than one
    /// external frontend should drop down to <see cref="AzureKubernetesEnvironmentExtensions.AddAzureKubernetesEnvironment(IDistributedApplicationBuilder, string)"/>
    /// and configure the gateway, routes, and cert-manager wiring directly.
    /// </para>
    /// <para>
    /// The defaults install Let's Encrypt production. For development loops that redeploy
    /// frequently, set <see cref="SimplifiedDeploymentOptions.AcmeEnvironment"/> to
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
    ///                  .WithSimplifiedDeployment(acmeEmail);
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///        .WithExternalHttpEndpoints();
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Configures the AKS environment with a complete VNet + AGC + cert-manager + TLS-enabled gateway in one call, and auto-routes every external HTTP endpoint", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithSimplifiedDeployment(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        IResourceBuilder<ParameterResource> acmeEmail,
        Action<SimplifiedDeploymentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(acmeEmail);

        // Materialize options up-front. Snapshot-style so later mutation of the options
        // bag (which we don't expose, but the callback could capture and re-invoke) cannot
        // drift the resources we register below.
        var options = new SimplifiedDeploymentOptions();
        configure?.Invoke(options);

        // Reject ambiguous string + parameter overrides up front. We could pick a
        // precedence and silently drop the loser, but that turns a config bug into a
        // mystery (the AppHost runs fine, just with the "wrong" value) — exactly the
        // class of footgun WithSimplifiedDeployment is here to remove. Validate now,
        // before any resources are added, so the message clearly identifies the
        // conflicting pair instead of surfacing later as a confusing infra error.
        ValidateOptions(options);

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
        var systemVmSize = ResolveVmSize(options.SystemNodePoolVmSize, options.SystemNodePoolVmSizeParameter);

        builder.WithSubnet(aksSubnet)
               .WithSystemNodePool(
                   systemVmSize,
                   minCount: options.SystemNodePoolMinCount,
                   maxCount: options.SystemNodePoolMaxCount);

        if (options.IncludeUserNodePool)
        {
            var userVmSize = ResolveVmSize(options.UserNodePoolVmSize, options.UserNodePoolVmSizeParameter);

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

        // Optional custom hostname. When set, the HTTPS listener binds to this name
        // (cert-manager will issue an LE cert for it) instead of the ALB-assigned
        // *.alb.azure.com hostname that the tls-fqdn-discovery step would otherwise
        // discover post-deploy. Both forms set Gateway.Hostnames, which short-circuits
        // the FQDN discovery step (it only runs for gateways with no hostname). The
        // string/parameter pair is validated mutually exclusive up top.
        if (options.HostnameParameter is not null)
        {
            gateway.WithHostname(options.HostnameParameter);
        }
        else if (!string.IsNullOrWhiteSpace(options.Hostname))
        {
            gateway.WithHostname(options.Hostname);
        }

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

        // 6. Auto-route external HTTP endpoints into the gateway. We attach an annotation
        //    that the K8s gateway-emission pipeline step honors, rather than subscribing to
        //    BeforePublishEvent. BeforePublishEvent only fires from PipelineExecutor (the
        //    legacy `dotnet run -- --publisher kubernetes` path); `aspire deploy` drives
        //    pipeline steps directly over the CLI JSON-RPC backchannel and never raises it,
        //    which previously left the gateway with no routes and silently skipped its
        //    manifest emission entirely.
        if (options.AutoRouteExternalEndpoints)
        {
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

            gateway.WithAnnotation(new KubernetesGatewayAutoRouteAnnotation(infraNames));
        }

        return builder;
    }

    // Default VM size for both node pools. The smallest AKS will accept for the
    // system pool that still leaves room for cert-manager, AGC's ALB controller,
    // kube-system, and CoreDNS without scheduling pressure. Reused for the user
    // pool so both pools have matching SKUs out of the box.
    private const string DefaultNodePoolVmSize = "Standard_D2as_v5";

    // Catch ambiguous "I set the string AND the parameter" calls up front so the
    // user sees one clear failure naming the conflicting pair, instead of getting
    // a silent "the other one won" surprise at deploy time.
    private static void ValidateOptions(SimplifiedDeploymentOptions options)
    {
        ValidatePair(
            nameof(SimplifiedDeploymentOptions.Hostname),
            options.Hostname is not null,
            nameof(SimplifiedDeploymentOptions.HostnameParameter),
            options.HostnameParameter is not null);

        ValidatePair(
            nameof(SimplifiedDeploymentOptions.SystemNodePoolVmSize),
            options.SystemNodePoolVmSize is not null,
            nameof(SimplifiedDeploymentOptions.SystemNodePoolVmSizeParameter),
            options.SystemNodePoolVmSizeParameter is not null);

        ValidatePair(
            nameof(SimplifiedDeploymentOptions.UserNodePoolVmSize),
            options.UserNodePoolVmSize is not null,
            nameof(SimplifiedDeploymentOptions.UserNodePoolVmSizeParameter),
            options.UserNodePoolVmSizeParameter is not null);
    }

    private static void ValidatePair(string stringName, bool stringSet, string parameterName, bool parameterSet)
    {
        if (stringSet && parameterSet)
        {
            throw new ArgumentException(
                $"Both {stringName} and {parameterName} were set on {nameof(SimplifiedDeploymentOptions)}. " +
                $"These options are mutually exclusive — use {stringName} for a hardcoded value or " +
                $"{parameterName} to bind the value to a deploy-time parameter, but not both.");
        }
    }

    // The underlying AKS node-pool API takes a plain string for VM size. When the
    // caller supplied a ParameterResource we resolve it synchronously here:
    // ParameterResource sources its value from configuration/env at AppHost startup
    // and doesn't depend on any other resource, so the blocking GetAwaiter().GetResult()
    // is safe (we're on the AppHost build thread, not a hot path).
    private static string ResolveVmSize(string? explicitSize, IResourceBuilder<ParameterResource>? parameterOverride)
    {
        if (parameterOverride is not null)
        {
            var value = parameterOverride.Resource.GetValueAsync(default).AsTask().GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return explicitSize ?? DefaultNodePoolVmSize;
    }
}
