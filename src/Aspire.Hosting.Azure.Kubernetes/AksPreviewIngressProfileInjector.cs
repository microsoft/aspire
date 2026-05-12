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
/// We use reflection because:
/// </para>
/// <list type="bullet">
///   <item>
///     <description><c>ManagedClusterIngressProfile</c> (the typed parent of
///     <c>gatewayAPI</c>/<c>applicationLoadBalancer</c>) is internal in
///     <c>Azure.Provisioning.ContainerService</c>, so we can't subclass it.</description>
///   </item>
///   <item>
///     <description>Calling <c>DefineProperty&lt;T&gt;</c> on a
///     <see cref="ContainerServiceManagedCluster"/> subclass with a path like
///     <c>["properties", "ingressProfile", "gatewayAPI", "installation"]</c> rewrites the
///     entire <c>properties</c> Bicep literal — the auto-rendered <c>dnsPrefix</c>,
///     <c>agentPoolProfiles</c>, <c>oidcIssuerProfile</c> and <c>securityProfile</c> are
///     dropped. The Provisioning emitter merges nested objects only when properties are
///     declared on the inner typed sub-object, not when overlaid via deep paths on the
///     outer resource.</description>
///   </item>
///   <item>
///     <description><c>DefineProperty&lt;T&gt;</c> called on the inner internal
///     <c>ManagedClusterIngressProfile</c> instance via reflection produces a properly
///     anchored <see cref="BicepValue{T}"/> whose path is rooted at that instance, which
///     merges correctly with sibling typed properties (e.g.
///     <c>ingressProfile.webAppRouting</c>).</description>
///   </item>
/// </list>
/// <para>
/// To make the inner <c>IngressProfile</c> instance exist (it is lazily created when the
/// public <see cref="ContainerServiceManagedCluster.IngressWebAppRouting"/> setter is
/// invoked), we assign an empty <see cref="ManagedClusterIngressProfileWebAppRouting"/>.
/// An empty inner <c>webAppRouting</c> object is filtered out by the emitter, so the
/// rendered Bicep does not gain a stray <c>webAppRouting: {}</c> block.
/// </para>
/// </remarks>
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
