// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // PipelineStepAnnotation/PipelineStep are evaluation-only
#pragma warning disable ASPIREAZURE003 // AzureSubnetResource is evaluation-only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Represents a single Azure Application Gateway for Containers (AGC)
/// <c>ApplicationLoadBalancer</c> Kubernetes custom resource (
/// <c>alb.networking.azure.io/v1</c>) bound to a delegated subnet.
/// </summary>
/// <remarks>
/// <para>
/// Each AGC <c>ApplicationLoadBalancer</c> is capped at 5 frontends, so larger
/// applications create multiple load balancer resources via repeated calls to
/// <see cref="AzureKubernetesEnvironmentExtensions.AddLoadBalancer"/> and associate
/// gateways/ingresses with a specific load balancer using
/// <see cref="AzureKubernetesIngressExtensions.WithLoadBalancer(global::Aspire.Hosting.ApplicationModel.IResourceBuilder{global::Aspire.Hosting.Kubernetes.KubernetesGatewayResource}, global::Aspire.Hosting.ApplicationModel.IResourceBuilder{AzureKubernetesLoadBalancerResource})"/>.
/// </para>
/// <para>
/// The resource registers a per-LB <c>apply-alb-crd-{name}</c> pipeline step that
/// runs after AKS credentials are fetched and before Helm chart preparation. The
/// step polls the cluster for the <c>azure-alb-external</c> GatewayClass (installed
/// by the AGC ALB controller add-on), then <c>kubectl apply</c>s the
/// <c>ApplicationLoadBalancer</c> custom resource pointing at the supplied subnet.
/// </para>
/// </remarks>
public sealed class AzureKubernetesLoadBalancerResource :
    Resource,
    IResourceWithParent<AzureKubernetesEnvironmentResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureKubernetesLoadBalancerResource"/> class.
    /// </summary>
    /// <param name="name">The name of the load balancer resource. Used to derive the in-cluster
    /// <c>ApplicationLoadBalancer</c> name (<c>alb-{name}</c>) referenced by gateway/ingress annotations.</param>
    /// <param name="parent">The parent AKS environment that owns this load balancer.</param>
    public AzureKubernetesLoadBalancerResource(string name, AzureKubernetesEnvironmentResource parent)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        Parent = parent;

        // Register the per-LB pipeline step that applies the ApplicationLoadBalancer
        // CR into the cluster after credentials are available. Using a factory lambda
        // so the step is materialized lazily by the pipeline configuration phase.
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var step = new PipelineStep
            {
                Name = $"apply-alb-crd-{Name}",
                Description = $"Applies the AGC ApplicationLoadBalancer CR for {Name}.",
                Action = ctx => Parent.ApplyAlbCrdAsync(this, ctx),
                // Kubeconfig must be set first.
                DependsOnSteps = [$"aks-get-credentials-{Parent.Name}"],
                // Helm prepare can then reference the LB by name in gateway/ingress annotations.
                RequiredBySteps = [$"prepare-{Parent.KubernetesEnvironment.Name}"]
            };

            return Task.FromResult<IEnumerable<PipelineStep>>([step]);
        }));
    }

    /// <inheritdoc />
    public AzureKubernetesEnvironmentResource Parent { get; }

    /// <summary>
    /// Reference to the resource ID of the delegated subnet that this load balancer
    /// associates with. Resolved at deployment time and emitted into the
    /// <c>spec.associations</c> field of the <c>ApplicationLoadBalancer</c> CR.
    /// </summary>
    internal BicepOutputReference SubnetIdReference { get; set; } = default!;

    /// <summary>
    /// The Aspire subnet resource that backs this load balancer. Captured so the AKS
    /// environment's Bicep emission can synthesize a per-LB role assignment granting
    /// the AKS-auto-created AGC controller identity the
    /// <c>Microsoft.Network/virtualNetworks/subnets/join/action</c> permission on the
    /// subnet (via <c>Network Contributor</c>). Without this, AKS only auto-grants the
    /// AGC identity permissions inside the cluster's <c>MC_*</c> node resource group, so
    /// any user-supplied subnet outside that RG fails with <c>LinkedAuthorizationFailed</c>
    /// when the controller tries to create the AGC association.
    /// </summary>
    internal Aspire.Hosting.Azure.AzureSubnetResource SubnetResource { get; set; } = default!;

    /// <summary>
    /// The in-cluster name of the <c>ApplicationLoadBalancer</c> CR. This is the value
    /// AGC expects in the <c>alb.networking.azure.io/alb-name</c> annotation on
    /// gateway/ingress resources.
    /// </summary>
    internal string AlbName => $"alb-{Name}";

    /// <summary>
    /// The Kubernetes namespace the <c>ApplicationLoadBalancer</c> CR is created in.
    /// Currently fixed to <c>default</c>; a future <c>WithNamespace</c> extension can
    /// surface this when multi-tenant clusters need isolation.
    /// </summary>
    internal static string AlbNamespace => "default";
}
