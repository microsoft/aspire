export interface WithPurgeTaskOptions {
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
    private async _withPurgeTaskInternal(schedule: string, filter?: string, ago?: number | null, keep?: number, taskName?: string): Promise<AzureContainerRegistryResource> {
            'Aspire.Hosting.Azure.ContainerRegistry/withPurgeTask',
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise {
        return new AzureContainerRegistryResourcePromiseImpl(this._withPurgeTaskInternal(schedule, filter, ago, keep, taskName), this._client);
    ["withPurgeTask"]: () => AzureContainerRegistryResourcePromiseImpl,