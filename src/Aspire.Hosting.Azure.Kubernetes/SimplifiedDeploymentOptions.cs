// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Knobs for
/// <see cref="AzureKubernetesSimplifiedDeploymentExtensions.WithSimplifiedDeployment"/>. All
/// properties have defaults that produce the "pit of success" AKS configuration —
/// every knob exists only so callers can deviate from that default when their
/// environment requires it, never because they have to set it to make the basic
/// case work.
/// </summary>
/// <remarks>
/// <para>
/// The defaults match the verbose recipe in <c>playground/CertManagerDemo</c>
/// (which this options bag is designed to replace): a <c>10.100.0.0/16</c> VNet
/// chosen to avoid the AKS default service CIDR <c>10.0.0.0/16</c>, a
/// <c>/22</c> AKS-node subnet sized for ≈1000 pods, and a <c>/24</c> AGC
/// frontend subnet (the smallest AGC allows).
/// </para>
/// <para>
/// Naming defaults (<see cref="LoadBalancerName"/>, <see cref="GatewayName"/>,
/// etc.) are short and generic on purpose so they read naturally as resource
/// names in the dashboard ("public", "public-gw", "cert-manager", "letsencrypt").
/// Override them when stacking multiple <c>WithSimplifiedDeployment</c>-style
/// recipes in one AppHost.
/// </para>
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class SimplifiedDeploymentOptions
{
    /// <summary>
    /// CIDR block for the auto-provisioned VNet. Defaults to <c>10.100.0.0/16</c>,
    /// chosen to avoid the AKS default service CIDR (<c>10.0.0.0/16</c>) so the
    /// pod and service networks don't collide.
    /// </summary>
    public string AddressSpace { get; set; } = "10.100.0.0/16";

    /// <summary>
    /// CIDR block for the AKS node-pool subnet. Defaults to <c>10.100.0.0/22</c>
    /// (1,024 IPs), which is enough headroom for the system node pool plus a
    /// modest workload node pool without bumping into the per-pod IP exhaustion
    /// failure mode.
    /// </summary>
    public string AksSubnetCidr { get; set; } = "10.100.0.0/22";

    /// <summary>
    /// CIDR block for the AGC public frontend subnet. Defaults to
    /// <c>10.100.4.0/24</c>. AGC requires a <c>/24</c> minimum and the subnet
    /// must be delegated to <c>Microsoft.ServiceNetworking/trafficControllers</c>
    /// (the underlying <see cref="AzureKubernetesEnvironmentExtensions.AddLoadBalancer"/>
    /// applies that delegation idempotently).
    /// </summary>
    public string LoadBalancerSubnetCidr { get; set; } = "10.100.4.0/24";

    /// <summary>
    /// VM size used for the AKS system node pool. When <see langword="null"/>
    /// (the default), <c>WithSimplifiedDeployment</c> uses <c>Standard_D2as_v5</c>
    /// — the smallest size AKS will accept for the system pool that still leaves
    /// room for cert-manager, AGC's ALB controller, kube-system, and CoreDNS
    /// without scheduling pressure.
    /// </summary>
    /// <remarks>
    /// Set this — or set <see cref="SystemNodePoolVmSizeParameter"/> for a
    /// deploy-time parameter — when the default SKU is not available in the target
    /// region or the subscription has insufficient vCPU quota for it. <c>az vm
    /// list-skus --location &lt;region&gt; --resource-type virtualMachines --output table</c>
    /// shows what's available in a region. Setting both this and
    /// <see cref="SystemNodePoolVmSizeParameter"/> throws because the intent is
    /// ambiguous — pick one.
    /// </remarks>
    public string? SystemNodePoolVmSize { get; set; }

    /// <summary>
    /// Optional parameter that, when set, supplies the system pool VM size at
    /// <c>WithSimplifiedDeployment</c> time. Lets the system pool SKU be swapped per
    /// environment via <c>aspire deploy -p systemVmSize=Standard_E2s_v5</c> without
    /// editing the AppHost — useful when a region runs out of quota for the default
    /// SKU and you need to fall back to whatever your subscription has headroom for.
    /// Setting both this and <see cref="SystemNodePoolVmSize"/> throws.
    /// </summary>
    [AspireExportIgnore(Reason = "Polyglot app hosts express parameter overrides differently.")]
    public IResourceBuilder<ParameterResource>? SystemNodePoolVmSizeParameter { get; set; }

    /// <summary>
    /// Minimum node count for the system node pool autoscaler. Defaults to 1.
    /// </summary>
    public int SystemNodePoolMinCount { get; set; } = 1;

    /// <summary>
    /// Maximum node count for the system node pool autoscaler. Defaults to 3.
    /// </summary>
    public int SystemNodePoolMaxCount { get; set; } = 3;

    /// <summary>
    /// When <see langword="true"/> (the default), <c>WithSimplifiedDeployment</c> also
    /// provisions a dedicated user node pool for application workloads so the system
    /// pool stays reserved for AKS system pods (cert-manager, CoreDNS, the AGC ALB
    /// controller). Set to <see langword="false"/> for the rare "tiny dev cluster"
    /// case where you want everything to schedule on the system pool.
    /// </summary>
    public bool IncludeUserNodePool { get; set; } = true;

    /// <summary>
    /// Name of the auto-created user node pool. Defaults to <c>"workload"</c>.
    /// </summary>
    public string UserNodePoolName { get; set; } = "workload";

    /// <summary>
    /// VM size for the auto-created user node pool. When <see langword="null"/>
    /// (the default), <c>WithSimplifiedDeployment</c> uses <c>Standard_D2as_v5</c>.
    /// </summary>
    /// <remarks>
    /// Set this — or set <see cref="UserNodePoolVmSizeParameter"/> — when the
    /// default SKU is not available in the target region or the subscription has
    /// insufficient vCPU quota for it. The user pool is typically what bumps into
    /// regional quota first since it scales with workload count. Setting both
    /// this and <see cref="UserNodePoolVmSizeParameter"/> throws because the
    /// intent is ambiguous — pick one.
    /// </remarks>
    public string? UserNodePoolVmSize { get; set; }

    /// <summary>
    /// Optional parameter that, when set, supplies the user pool VM size at
    /// <c>WithSimplifiedDeployment</c> time. Lets the workload pool SKU be swapped per
    /// environment via <c>aspire deploy -p userVmSize=Standard_E2s_v5</c> without
    /// editing the AppHost. Setting both this and <see cref="UserNodePoolVmSize"/> throws.
    /// </summary>
    [AspireExportIgnore(Reason = "Polyglot app hosts express parameter overrides differently.")]
    public IResourceBuilder<ParameterResource>? UserNodePoolVmSizeParameter { get; set; }

    /// <summary>
    /// Minimum node count for the user node pool autoscaler. Defaults to 1.
    /// </summary>
    public int UserNodePoolMinCount { get; set; } = 1;

    /// <summary>
    /// Maximum node count for the user node pool autoscaler. Defaults to 3.
    /// </summary>
    public int UserNodePoolMaxCount { get; set; } = 3;

    /// <summary>
    /// Name of the auto-created <see cref="AzureKubernetesLoadBalancerResource"/>.
    /// Defaults to <c>"public"</c>.
    /// </summary>
    public string LoadBalancerName { get; set; } = "public";

    /// <summary>
    /// Name of the auto-created <see cref="KubernetesGatewayResource"/>. Defaults
    /// to <c>"public-gw"</c>.
    /// </summary>
    public string GatewayName { get; set; } = "public-gw";

    /// <summary>
    /// Name of the auto-created <see cref="CertManagerResource"/>. Defaults to
    /// <c>"cert-manager"</c>.
    /// </summary>
    public string CertManagerName { get; set; } = "cert-manager";

    /// <summary>
    /// Name of the auto-created <see cref="CertManagerIssuerResource"/>. Defaults
    /// to <c>"letsencrypt"</c>.
    /// </summary>
    public string IssuerName { get; set; } = "letsencrypt";

    /// <summary>
    /// Which Let's Encrypt environment the auto-provisioned issuer points at.
    /// Defaults to <see cref="LetsEncryptEnvironment.Production"/>.
    /// Switch to <see cref="LetsEncryptEnvironment.Staging"/> for
    /// development loops where you'd otherwise burn the production rate limit.
    /// </summary>
    public LetsEncryptEnvironment AcmeEnvironment { get; set; } = LetsEncryptEnvironment.Production;

    /// <summary>
    /// When <see langword="true"/> (the default), the auto-created gateway is
    /// configured for TLS via <see cref="KubernetesGatewayExtensions.WithTls(global::Aspire.Hosting.ApplicationModel.IResourceBuilder{KubernetesGatewayResource}, System.Action{TlsOptions}?)"/>
    /// (and the cert-manager + issuer resources are provisioned). Set to
    /// <see langword="false"/> for the rare case where the cluster needs a plain
    /// HTTP gateway — typically development against a non-public hostname where
    /// Let's Encrypt HTTP-01 validation cannot succeed.
    /// </summary>
    public bool EnableTls { get; set; } = true;

    /// <summary>
    /// Optional callback for tuning the TLS posture (HTTP→HTTPS redirect, HSTS
    /// directives). Forwarded directly to <c>gateway.WithTls(issuer, configure)</c>;
    /// see <see cref="TlsOptions"/> for the available knobs.
    /// </summary>
    public Action<TlsOptions>? ConfigureTls { get; set; }

    /// <summary>
    /// When <see langword="true"/> (the default), each resource in the
    /// application model that exposes one or more <c>IsExternal == true</c>
    /// endpoints is automatically attached to the auto-gateway via
    /// <c>WithRoute</c>.
    /// Resources that the user has already wired up by hand are skipped (the
    /// user always wins).
    /// </summary>
    public bool AutoRouteExternalEndpoints { get; set; } = true;

    /// <summary>
    /// Optional hostname that the auto-gateway should listen on (and that the
    /// auto-issued certificate covers). When unset (the default), the gateway
    /// listens on the ALB-assigned <c>*.alb.azure.com</c> hostname that the
    /// <c>tls-fqdn-discovery</c> pipeline step discovers post-deploy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting a custom hostname binds the HTTPS listener to that name and
    /// asks cert-manager to issue a Let's Encrypt cert for it. The plain-HTTP
    /// listener stays unbound to any hostname so the ACME HTTP-01 challenge
    /// and the HTTP→HTTPS redirect keep working regardless of which name a
    /// request arrives under.
    /// </para>
    /// <para>
    /// <b>DNS chicken-and-egg:</b> Let's Encrypt's HTTP-01 challenge requires
    /// the hostname to already resolve to the ALB at validation time. On the
    /// first deploy:
    /// <list type="number">
    ///   <item>Run <c>aspire deploy</c>; the ALB is provisioned and assigned
    ///     a public hostname (e.g. <c>*.alb.azure.com</c>) but the cert stays
    ///     <c>Pending</c>.</item>
    ///   <item>Read that ALB hostname from the deploy output (or
    ///     <c>kubectl get gateway</c>) and create a CNAME from
    ///     <c><see cref="Hostname"/></c> to it.</item>
    ///   <item>cert-manager retries automatically; the cert issues and the
    ///     listener becomes ready.</item>
    /// </list>
    /// Once the CNAME is in place subsequent deploys are uneventful.
    /// </para>
    /// <para>
    /// Setting both this and <see cref="HostnameParameter"/> throws because the
    /// intent is ambiguous — pick one. Use the parameter form when the hostname
    /// must vary by environment (<c>aspire deploy -p hostname=app.contoso.com</c>).
    /// </para>
    /// </remarks>
    public string? Hostname { get; set; }

    /// <summary>
    /// Optional parameter that, when set, supplies the gateway hostname at
    /// <c>WithSimplifiedDeployment</c> time. Use this to keep the hostname out
    /// of source and pass it in via <c>aspire deploy -p hostname=app.contoso.com</c>.
    /// Setting both this and <see cref="Hostname"/> throws.
    /// </summary>
    /// <remarks>
    /// See <see cref="Hostname"/> for the DNS chicken-and-egg flow on first
    /// deploy. The same constraints apply when the value is supplied via
    /// parameter.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts express parameter overrides differently.")]
    public IResourceBuilder<ParameterResource>? HostnameParameter { get; set; }
}
