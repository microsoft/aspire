// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.ContainerService;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Adds the preview-only application load balancer property to the AKS ingress profile.
/// </summary>
internal sealed class AksPreviewIngressProfile : ManagedClusterIngressProfile
{
    private BicepValue<bool>? _applicationLoadBalancerEnabled;

    public BicepValue<bool> ApplicationLoadBalancerEnabled
    {
        get
        {
            Initialize();
            return _applicationLoadBalancerEnabled!;
        }
        set
        {
            Initialize();
            _applicationLoadBalancerEnabled!.Assign(value);
        }
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();

        // The SDK omits this preview-only property. Keep it on the ingress profile so
        // it merges with the typed gatewayAPI configuration instead of shadowing it.
        // https://github.com/Azure/azure-sdk-for-net/issues/59225
        _applicationLoadBalancerEnabled = DefineProperty<bool>(
            nameof(ApplicationLoadBalancerEnabled), ["applicationLoadBalancer", "enabled"]);
    }
}
