// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Azure.Provisioning;
using Azure.Provisioning.ContainerService;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Injects the AKS preview-only <c>properties.ingressProfile.gatewayAPI.installation</c>
/// and <c>properties.ingressProfile.applicationLoadBalancer.enabled</c> Bicep properties
/// onto a <see cref="ContainerServiceManagedCluster"/>. These properties only exist on the
/// <c>2025-08-02-preview</c> (gatewayAPI) and <c>2025-09-02-preview</c>
/// (applicationLoadBalancer) AKS API versions; the latest stable <c>2026-01-01</c> and
/// <c>Azure.Provisioning.ContainerService 1.0.0-beta.6</c> expose neither.
/// </summary>
/// <remarks>
/// <para>
/// Reflection is unavoidable here. The Provisioning emitter merges sibling property
/// declarations only when they are registered on the same <see cref="ProvisionableConstruct"/>
/// instance. <c>ManagedClusterIngressProfile</c> (the typed parent of <c>gatewayAPI</c> and
/// <c>applicationLoadBalancer</c>) is <c>internal</c> in
/// <c>Azure.Provisioning.ContainerService 1.0.0-beta.6</c>, so we cannot subclass it to add
/// the missing properties through the normal public extension pattern (compare
/// <c>ContainerAppEnvironmentDotnetComponentResource</c> in
/// <c>Aspire.Hosting.Azure.AppContainers</c>, <c>CosmosDBSqlRoleAssignment_Derived</c> in
/// <c>Aspire.Hosting.Azure.CosmosDB</c>, and
/// <c>PublicHostingCognitiveServicesCapabilityHostProperties</c> in
/// <c>Aspire.Hosting.Foundry</c>, all of which extend public typed parents).
/// </para>
/// <para>
/// Two reflection-free alternatives were attempted and empirically ruled out:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Subclassing <see cref="ContainerServiceManagedCluster"/> and calling
///       <c>DefineProperty&lt;T&gt;</c> with a deep path such as
///       <c>["properties", "ingressProfile", "gatewayAPI", "installation"]</c>. The deeper
///       declaration shadows the typed <c>Properties</c> declaration on the base, so the
///       emitted Bicep loses <c>dnsPrefix</c>, <c>agentPoolProfiles</c>,
///       <c>oidcIssuerProfile</c> and <c>securityProfile</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       Same subclass plus <c>DefineModelProperty&lt;T&gt;(..., ["properties", "ingressProfile"], new T())</c>
///       grafting a public <see cref="ProvisionableConstruct"/> that internally registers
///       <c>["gatewayAPI", "installation"]</c> and <c>["applicationLoadBalancer", "enabled"]</c>.
///       The grafted model emits its own properties correctly, but still shadows the typed
///       <c>Properties</c> declaration on the base for the same reason as (1).
///     </description>
///   </item>
/// </list>
/// <para>
/// The reflection path works because <c>DefineProperty&lt;T&gt;</c> called on the inner
/// internal <c>ManagedClusterIngressProfile</c> instance produces a
/// <see cref="BicepValue{T}"/> whose path is rooted at that instance — it merges with the
/// sibling typed <c>webAppRouting</c> declaration on the same construct rather than
/// shadowing the cluster-level <c>Properties</c>.
/// </para>
/// <para>
/// To make the inner <c>IngressProfile</c> instance exist (it is lazily created when the
/// public <see cref="ContainerServiceManagedCluster.IngressWebAppRouting"/> setter is
/// invoked), we assign an empty <see cref="ManagedClusterIngressProfileWebAppRouting"/>.
/// An empty inner <c>webAppRouting</c> object is filtered out by the emitter, so the
/// rendered Bicep does not gain a stray <c>webAppRouting: {}</c> block.
/// </para>
/// <para>
/// The proper long-term fix is for <c>Azure.Provisioning.ContainerService</c> to expose
/// <c>ManagedClusterIngressProfile</c> publicly (or to add typed <c>GatewayApi</c> and
/// <c>ApplicationLoadBalancer</c> properties on it). When that ships, this class can be
/// replaced with the standard public-subclass pattern and deleted.
/// </para>
/// <para>
/// Tracked by <see href="https://github.com/microsoft/aspire/issues/17060"/> (Aspire) and
/// <see href="https://github.com/Azure/azure-sdk-for-net/issues/59225"/> (upstream).
/// </para>
/// </remarks>
// TODO: https://github.com/microsoft/aspire/issues/17060 - delete this class once
// Azure.Provisioning.ContainerService exposes ManagedClusterIngressProfile publicly
// with typed GatewayApi and ApplicationLoadBalancer properties.
internal static class AksPreviewIngressProfileInjector
{
    private const string IngressProfileTypeFullName = "Azure.Provisioning.ContainerService.ManagedClusterIngressProfile";

    private static readonly Lazy<MethodInfo> s_defineProperty = new(() =>
    {
        // protected BicepValue<T> DefineProperty<T>(string propertyName, string[] bicepPath, bool isOutput = false, bool isRequired = false, bool isSecure = false, BicepValue<T>? defaultValue = null, string? format = null)
        return typeof(ProvisionableConstruct).GetMethod(
            "DefineProperty",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ProvisionableConstruct.DefineProperty<T> not found via reflection. The Azure.Provisioning surface may have changed; review AksPreviewIngressProfileInjector.");
    });

    /// <summary>
    /// Injects the requested preview-only ingressProfile entries onto <paramref name="aks"/>.
    /// Caller is responsible for setting an appropriate preview <c>ResourceVersion</c> on
    /// the cluster (e.g. <c>2025-09-02-preview</c>) before any properties are compiled.
    /// </summary>
    public static void Inject(ContainerServiceManagedCluster aks, bool gatewayApi, bool applicationLoadBalancer)
    {
        if (!gatewayApi && !applicationLoadBalancer)
        {
            return;
        }

        // Bootstrap the lazily-created internal IngressProfile by assigning an empty
        // WebAppRouting object via the public setter. An empty WebAppRouting object is
        // filtered out at emission time, so this does not introduce a stray webAppRouting
        // entry into the rendered Bicep.
        aks.IngressWebAppRouting = new ManagedClusterIngressProfileWebAppRouting();

        var ingressProfile = GetIngressProfileInstance(aks);
        var defineProperty = s_defineProperty.Value;

        if (gatewayApi)
        {
            // properties.ingressProfile.gatewayAPI.installation = 'Standard'
            // The only AKS-managed installation value is "Standard"; it installs the
            // upstream Gateway API CRDs and the AKS-managed Gateway controller.
            var installation = (BicepValue<string>)defineProperty
                .MakeGenericMethod(typeof(string))
                .Invoke(ingressProfile, [
                    "GatewayAPIInstallation",
                    new[] { "gatewayAPI", "installation" },
                    /* isOutput */ false,
                    /* isRequired */ false,
                    /* isSecure */ false,
                    /* defaultValue */ null,
                    /* format */ null,
                ])!;
            installation.Assign("Standard");
        }

        if (applicationLoadBalancer)
        {
            // properties.ingressProfile.applicationLoadBalancer.enabled = true
            // Enables the AKS-managed AGC ALB controller add-on (which installs the
            // azure-alb-external GatewayClass and watches for ApplicationLoadBalancer CRs).
            var enabled = (BicepValue<bool>)defineProperty
                .MakeGenericMethod(typeof(bool))
                .Invoke(ingressProfile, [
                    "ApplicationLoadBalancerEnabled",
                    new[] { "applicationLoadBalancer", "enabled" },
                    /* isOutput */ false,
                    /* isRequired */ false,
                    /* isSecure */ false,
                    /* defaultValue */ null,
                    /* format */ null,
                ])!;
            enabled.Assign(true);
        }
    }

    private static object GetIngressProfileInstance(ContainerServiceManagedCluster aks)
    {
        // ContainerServiceManagedCluster.Properties is internal and exposes the
        // ManagedClusterProperties complex object that owns IngressProfile (also internal).
        var clusterPropsProp = typeof(ContainerServiceManagedCluster).GetProperty(
            "Properties",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ContainerServiceManagedCluster.Properties not found via reflection. The Azure.Provisioning surface may have changed; review AksPreviewIngressProfileInjector.");
        var clusterProps = clusterPropsProp.GetValue(aks)
            ?? throw new InvalidOperationException("ContainerServiceManagedCluster.Properties returned null.");

        var ipProp = clusterProps.GetType().GetProperty(
            "IngressProfile",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ManagedClusterProperties.IngressProfile not found via reflection. The Azure.Provisioning surface may have changed; review AksPreviewIngressProfileInjector.");
        var ipInstance = ipProp.GetValue(clusterProps)
            ?? throw new InvalidOperationException("ManagedClusterProperties.IngressProfile was null even after assigning IngressWebAppRouting; the Azure.Provisioning lazy-initialization behavior may have changed.");

        if (ipInstance.GetType().FullName != IngressProfileTypeFullName)
        {
            throw new InvalidOperationException($"Expected IngressProfile to be a {IngressProfileTypeFullName} but found {ipInstance.GetType().FullName}.");
        }

        return ipInstance;
    }
}
