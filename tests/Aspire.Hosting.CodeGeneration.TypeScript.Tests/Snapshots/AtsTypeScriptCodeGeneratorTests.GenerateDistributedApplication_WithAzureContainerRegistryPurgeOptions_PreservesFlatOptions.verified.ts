export interface WithPurgeTaskOptions {
    /** Whether to also delete eligible untagged manifests. Defaults to true. */
    untagged?: boolean;
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. By default, `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Set `untagged` to false to remove tags only.
     * The task uses ACR CLI 0.20 with separate tag and untagged-manifest count limits. Untagged cleanup retains no
     * additional untagged manifests, regardless of age. Locked artifacts and dependencies of retained images are protected,
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. By default, `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Set `untagged` to false to remove tags only.
     * The task uses ACR CLI 0.20 with separate tag and untagged-manifest count limits. Untagged cleanup retains no
     * additional untagged manifests, regardless of age. Locked artifacts and dependencies of retained images are protected,
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
    private async _withPurgeTaskInternal(schedule: string, filter?: string, ago?: number | null, keep?: number, taskName?: string, untagged?: boolean): Promise<AzureContainerRegistryResource> {
        if (untagged !== undefined) rpcArgs.untagged = untagged;
            'Aspire.Hosting.Azure.ContainerRegistry/withPurgeTask',
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. By default, `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Set `untagged` to false to remove tags only.
     * The task uses ACR CLI 0.20 with separate tag and untagged-manifest count limits. Untagged cleanup retains no
     * additional untagged manifests, regardless of age. Locked artifacts and dependencies of retained images are protected,
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise {
        const untagged = options?.untagged;
        return new AzureContainerRegistryResourcePromiseImpl(this._withPurgeTaskInternal(schedule, filter, ago, keep, taskName, untagged), this._client);
    ["withPurgeTask"]: () => AzureContainerRegistryResourcePromiseImpl,