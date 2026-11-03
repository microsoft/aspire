 * eligible untagged manifests. Image layers shared with retained manifests remain in the registry.
     * to tags and untagged manifests. This is not a count of deployable application versions.
     * Gets or initializes whether to delete eligible untagged manifests in addition to tags. Defaults to `false`.
     * This also selects previously untagged manifests in repositories matched by `Filter`,
     * not just manifests untagged by this task. Deletion is irreversible and can break digest-based
     * The purge tool's version determines how age and keep filters apply to untagged manifests.
    untagged?: boolean;
    dryRun?: boolean;
export interface WithPurgeTaskOptions {
    /** The number of recent purge candidates to retain per repository. Defaults to 3. The purge tool's version determines how this applies to untagged manifests. */
    /** Whether to also delete eligible untagged manifests in matching repositories. Deletion is irreversible and can break digest-based pulls and rollbacks. Defaults to false. */
    untagged?: boolean;
    dryRun?: boolean;
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise;
    private async _withPurgeTaskInternal(schedule: string, filter?: string, ago?: number | null, keep?: number, taskName?: string, untagged?: boolean, dryRun?: boolean): Promise<AzureContainerRegistryResource> {
        if (untagged !== undefined) rpcArgs.untagged = untagged;
        if (dryRun !== undefined) rpcArgs.dryRun = dryRun;
            'Aspire.Hosting.Azure.ContainerRegistry/withPurgeTask',
     * Adds a scheduled ACR purge task to remove image tags and optionally untagged manifests.
    withPurgeTask(schedule: string, options?: WithPurgeTaskOptions): AzureContainerRegistryResourcePromise {
        const untagged = options?.untagged;
        const dryRun = options?.dryRun;
        return new AzureContainerRegistryResourcePromiseImpl(this._withPurgeTaskInternal(schedule, filter, ago, keep, taskName, untagged, dryRun), this._client);
    ["withPurgeTask"]: () => AzureContainerRegistryResourcePromiseImpl,