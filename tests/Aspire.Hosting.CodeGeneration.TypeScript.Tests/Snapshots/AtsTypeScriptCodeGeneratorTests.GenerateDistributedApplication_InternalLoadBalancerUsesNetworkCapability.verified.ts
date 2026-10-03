    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureContainerAppEnvironmentResourcePromise;
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureContainerAppEnvironmentResourcePromise;
    private async _withInternalLoadBalancerInternal(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): Promise<AzureContainerAppEnvironmentResource> {
            'Aspire.Hosting.Azure.Network/withNetworkInternalLoadBalancer',
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureContainerAppEnvironmentResourcePromise {
        return new AzureContainerAppEnvironmentResourcePromiseImpl(this._withInternalLoadBalancerInternal(virtualNetwork), this._client);
    ["withInternalLoadBalancer"]: () => AzureContainerAppEnvironmentResourcePromiseImpl,
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureInternalLoadBalancerResourcePromise;
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureInternalLoadBalancerResourcePromise;
    private async _withInternalLoadBalancerInternal(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): Promise<AzureInternalLoadBalancerResource> {
            'Aspire.Hosting.Azure.Network/withNetworkInternalLoadBalancer',
    withInternalLoadBalancer(virtualNetwork: Awaitable<AzureVirtualNetworkResource>): AzureInternalLoadBalancerResourcePromise {
        return new AzureInternalLoadBalancerResourcePromiseImpl(this._withInternalLoadBalancerInternal(virtualNetwork), this._client);
    ["withInternalLoadBalancer"]: () => AzureInternalLoadBalancerResourcePromiseImpl,