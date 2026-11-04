export interface WithPurgeTaskOptions {
    /** Whether to also delete eligible untagged manifests. Defaults to true. */
    untagged?: boolean;
    cliVersion?: string;
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. By default, `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Set `untagged` to false to remove tags only.
     * The task uses ACR CLI 0.21 by default with a minimum tag count and a separate untagged-manifest count limit. Untagged cleanup retains no
     * additional untagged manifests, regardless of age. Locked artifacts and dependencies of retained images are protected,
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. By default, `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Set `untagged` to false to remove tags only.
     * The task uses ACR CLI 0.21 by default with a minimum tag count and a separate untagged-manifest count limit. Untagged cleanup retains no
     * additional untagged manifests, regardless of age. Locked artifacts and dependencies of retained images are protected,
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
    private async _withPurgeTaskInternal(schedule: string, filter?: string, ago?: number | null, keep?: number, taskName?: string, untagged?: boolean, cliVersion?: string): Promise<AzureContainerRegistryResource> {
        if (untagged !== undefined) rpcArgs.untagged = untagged;
        if (cliVersion !== undefined) rpcArgs.cliVersion = cliVersion;
            'Aspire.Hosting.Azure.ContainerRegistry/withPurgeTask',
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. By default, `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Set `untagged` to false to remove tags only.
     * The task uses ACR CLI 0.21 by default with a minimum tag count and a separate untagged-manifest count limit. Untagged cleanup retains no
     * additional untagged manifests, regardless of age. Locked artifacts and dependencies of retained images are protected,
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise {
        const untagged = options?.untagged;
        const cliVersion = options?.cliVersion;
        return new AzureContainerRegistryResourcePromiseImpl(this._withPurgeTaskInternal(schedule, filter, ago, keep, taskName, untagged, cliVersion), this._client);
    ["withPurgeTask"]: () => AzureContainerRegistryResourcePromiseImpl,