import type { DistributedApplicationPipelinePromise } from '../.aspire/modules/aspire.mjs';
import { exec } from '../lib/process.mjs';
import type { RepoRoot } from '../lib/repo.mjs';

// Shadows .github/workflows/build-packages.yml.
export function addPackagingWorkflow(
    pipeline: DistributedApplicationPipelinePromise,
    repoRoot: RepoRoot): DistributedApplicationPipelinePromise {
    return pipeline
        .addStep('ci-pack', async () => { })
        .addStep('pack', async context => {
            await exec(context, repoRoot, {
                title: 'Building packages',
                command: './build.sh',
                args: [
                    '-restore',
                    '-build',
                    '-ci',
                    '-pack',
                    '-bl',
                    '-p:InstallBrowsersForPlaywright=false',
                    '-p:SkipTestProjects=true',
                    '-p:SkipPlaygroundProjects=true',
                    '-p:SkipBundleDeps=true'
                ]
            });
        }, {
            requiredBy: ['ci-pack']
        });
}
