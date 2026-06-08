import type { DistributedApplicationPipelinePromise } from '../.aspire/modules/aspire.mjs';
import { exec } from '../lib/process.mjs';
import type { RepoRoot } from '../lib/repo.mjs';

// Shadows .github/workflows/typescript-sdk-tests.yml and .github/workflows/verify-aspire-skills-bundle.yml.
export function addFastWorkflow(
    pipeline: DistributedApplicationPipelinePromise,
    repoRoot: RepoRoot): DistributedApplicationPipelinePromise {
    return pipeline
        .addStep('ci-fast', async () => { })
        .addStep('typescript-sdk-tests', async context => {
            await exec(context, repoRoot, {
                title: 'Installing TypeScript SDK test dependencies',
                command: 'npm',
                args: ['ci'],
                workingDirectory: 'tests/Aspire.Hosting.CodeGeneration.TypeScript.JsTests'
            });
            await exec(context, repoRoot, {
                title: 'Running TypeScript SDK tests',
                command: 'npx',
                args: ['vitest', 'run', '--reporter=verbose'],
                workingDirectory: 'tests/Aspire.Hosting.CodeGeneration.TypeScript.JsTests'
            });
        }, {
            requiredBy: ['ci-fast']
        })
        .addStep('verify-skills-bundle', async context => {
            await exec(context, repoRoot, {
                title: 'Verifying embedded Aspire skills bundle',
                command: 'pwsh',
                args: ['./eng/scripts/verify-aspire-skills-bundle.ps1']
            });
        }, {
            requiredBy: ['ci-fast']
        });
}
