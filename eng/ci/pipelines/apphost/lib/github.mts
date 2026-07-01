export interface GitHubActionsContext {
    getBuildxCacheArgs(cacheScope: string): string[];
}

export const githubActions: GitHubActionsContext = {
    getBuildxCacheArgs(cacheScope) {
        if (process.env.ACTIONS_RUNTIME_TOKEN === undefined ||
            (process.env.ACTIONS_CACHE_URL === undefined && process.env.ACTIONS_RESULTS_URL === undefined)) {
            return [];
        }

        return [
            '--cache-from',
            `type=gha,scope=${cacheScope}`,
            '--cache-to',
            `type=gha,scope=${cacheScope},mode=max,ignore-error=true`
        ];
    }
};
