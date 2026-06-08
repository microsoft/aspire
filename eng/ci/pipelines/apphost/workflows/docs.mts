import type { DistributedApplicationPipelinePromise } from '../.aspire/modules/aspire.mjs';
import { exec } from '../lib/process.mjs';
import type { RepoRoot } from '../lib/repo.mjs';

// Shadows .github/workflows/markdownlint.yml.
export function addDocsWorkflow(
    pipeline: DistributedApplicationPipelinePromise,
    repoRoot: RepoRoot): DistributedApplicationPipelinePromise {
    return pipeline
        .addStep('ci-docs', async () => { })
        .addStep('markdownlint', async context => {
            await exec(context, repoRoot, {
                title: 'Running Markdownlint',
                command: 'npm',
                args: [
                    'exec',
                    '--yes',
                    '--package',
                    'markdownlint-cli@0.45.0',
                    '--',
                    'markdownlint',
                    '--ignore',
                    '.dotnet/',
                    '--ignore',
                    'tools/',
                    '--ignore',
                    '**/node_modules/**',
                    '--ignore',
                    '**/AnalyzerReleases.*.md',
                    '**/*.md'
                ],
                env: {
                    NPM_CONFIG_USERCONFIG: repoRoot.path('eng/ci/pipelines/apphost/.npmrc.empty'),
                    NPM_CONFIG_REGISTRY: 'https://registry.npmjs.org/'
                }
            });
        }, {
            requiredBy: ['ci-docs']
        });
}
