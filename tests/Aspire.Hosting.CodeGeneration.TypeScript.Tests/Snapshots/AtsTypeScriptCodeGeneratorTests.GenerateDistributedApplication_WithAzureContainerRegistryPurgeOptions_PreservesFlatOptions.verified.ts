export interface WithPurgeTaskOptions {
    /** Whether to also delete eligible untagged manifests. Defaults to false. */
    untagged?: boolean;
    dryRun?: boolean;
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. Enabling `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Preview deletions with `dryRun` first.
     * The purge tool's version determines how age and keep filters apply to untagged manifests.
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. Enabling `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Preview deletions with `dryRun` first.
     * The purge tool's version determines how age and keep filters apply to untagged manifests.
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
    private async _withPurgeTaskInternal(schedule: string, filter?: string, ago?: number | null, keep?: number, taskName?: string, untagged?: boolean, dryRun?: boolean): Promise<AzureContainerRegistryResource> {
        if (untagged !== undefined) rpcArgs.untagged = untagged;
        if (dryRun !== undefined) rpcArgs.dryRun = dryRun;
            'Aspire.Hosting.Azure.ContainerRegistry/withPurgeTask',
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
     * Tag removal alone does not reclaim storage. Enabling `untagged` also deletes eligible
     * previously untagged manifests and can break digest-based pulls and rollbacks. No Premium SKU or retention
     * policy is required. Preview deletions with `dryRun` first.
     * The purge tool's version determines how age and keep filters apply to untagged manifests.
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise {
        const untagged = options?.untagged;
        const dryRun = options?.dryRun;
        return new AzureContainerRegistryResourcePromiseImpl(this._withPurgeTaskInternal(schedule, filter, ago, keep, taskName, untagged, dryRun), this._client);
    ["withPurgeTask"]: () => AzureContainerRegistryResourcePromiseImpl,