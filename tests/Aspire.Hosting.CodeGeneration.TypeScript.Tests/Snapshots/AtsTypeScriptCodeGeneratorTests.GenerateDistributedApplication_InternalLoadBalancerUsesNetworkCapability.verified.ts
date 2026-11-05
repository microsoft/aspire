    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureContainerAppEnvironmentResourcePromise;
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureContainerAppEnvironmentResourcePromise;
    private async _withInternalLoadBalancerInternal(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): Promise<AzureContainerAppEnvironmentResource> {
            'Aspire.Hosting.Azure.Network/withNetworkInternalLoadBalancer',
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureContainerAppEnvironmentResourcePromise {
        return new AzureContainerAppEnvironmentResourcePromiseImpl(this._withInternalLoadBalancerInternal(virtualNetwork), this._client);
    ["withInternalLoadBalancer"]: () => AzureContainerAppEnvironmentResourcePromiseImpl,
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureInternalIngressResourcePromise;
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureInternalIngressResourcePromise;
    private async _withInternalLoadBalancerInternal(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): Promise<AzureInternalIngressResource> {
            'Aspire.Hosting.Azure.Network/withNetworkInternalLoadBalancer',
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureInternalIngressResourcePromise {
        return new AzureInternalIngressResourcePromiseImpl(this._withInternalLoadBalancerInternal(virtualNetwork), this._client);
    ["withInternalLoadBalancer"]: () => AzureInternalIngressResourcePromiseImpl,