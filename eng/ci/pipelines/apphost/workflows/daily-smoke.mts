import type { DistributedApplicationPipelinePromise } from '../.aspire/modules/aspire.mjs';
import { verifyDocker } from '../lib/docker.mjs';
import { dotnet } from '../lib/dotnet.mjs';
import { readEnv } from '../lib/env.mjs';
import { restoreRepository, type RepoRoot } from '../lib/repo.mjs';
import { loadCliE2EImages, readCliE2EImageEnvironmentFile } from './cli-e2e-helpers.mjs';

// Shadows .github/workflows/tests-daily-smoke.yml.
interface DailySmokeSettings {
    quality: string;
    imageDirectory: string;
    testResultsDirectory: string;
    cliVersionOutputDirectory: string;
    loadedImageEnvFile: string;
}

export function addDailySmokeWorkflow(
    pipeline: DistributedApplicationPipelinePromise,
    repoRoot: RepoRoot): DistributedApplicationPipelinePromise {
    return pipeline
        .addStep('ci-daily-smoke', async () => { })
        .addStep('daily-smoke-restore', async context => {
            await restoreRepository(context, repoRoot);
        }, {
            requiredBy: ['ci-daily-smoke']
        })
        .addStep('daily-smoke-verify-docker', async context => {
            await verifyDocker(context, repoRoot);
        }, {
            dependsOn: ['daily-smoke-restore'],
            requiredBy: ['ci-daily-smoke']
        })
        .addStep('daily-smoke-load-cli-e2e-image', async context => {
            const settings = getDailySmokeSettings(repoRoot);
            await loadCliE2EImages(context, repoRoot, {
                imageDirectory: settings.imageDirectory,
                githubEnvFile: settings.loadedImageEnvFile,
                requireDotnet: true
            });
        }, {
            dependsOn: ['daily-smoke-verify-docker'],
            requiredBy: ['ci-daily-smoke']
        })
        .addStep('daily-smoke-build-tests', async context => {
            await dotnet(
                context,
                repoRoot,
                'build',
                ['tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj'],
                { title: 'Building CLI E2E test project' });
        }, {
            dependsOn: ['daily-smoke-load-cli-e2e-image'],
            requiredBy: ['ci-daily-smoke']
        })
        .addStep('daily-smoke-tests', async context => {
            const settings = getDailySmokeSettings(repoRoot);
            const loadedImageEnv = await readCliE2EImageEnvironmentFile(settings.loadedImageEnvFile);

            await dotnet(
                context,
                repoRoot,
                'test',
                [
                    '--project',
                    'tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj',
                    '--no-build',
                    '--report-trx',
                    '--report-trx-filename',
                    'DailyCliSmoke.trx',
                    '--results-directory',
                    settings.testResultsDirectory,
                    '--filter-class',
                    '*SmokeTests',
                    '--filter-not-trait',
                    'quarantined=true',
                    '--filter-not-trait',
                    'outerloop=true',
                    '--hangdump',
                    '--hangdump-type',
                    'none',
                    '--hangdump-timeout',
                    '15m'
                ],
                {
                    title: `Running CLI smoke tests against ${settings.quality} build`,
                    env: {
                        ...loadedImageEnv,
                        ASPIRE_E2E_QUALITY: settings.quality,
                        ASPIRE_E2E_CLI_VERSION_OUTPUT_DIR: settings.cliVersionOutputDirectory
                    }
                });
        }, {
            dependsOn: ['daily-smoke-build-tests'],
            requiredBy: ['ci-daily-smoke']
        });
}

function getDailySmokeSettings(repoRoot: RepoRoot): DailySmokeSettings {
    const testResultsDirectory = readEnv('SMOKE_TEST_RESULTS_DIR', repoRoot.path('testresults'));

    return {
        quality: readEnv('ASPIRE_E2E_QUALITY', 'dev'),
        imageDirectory: readEnv('CLI_E2E_IMAGE_DIR', repoRoot.path('cli-e2e-image')),
        testResultsDirectory,
        cliVersionOutputDirectory: readEnv('ASPIRE_E2E_CLI_VERSION_OUTPUT_DIR', repoRoot.path('testresults/cli-versions')),
        loadedImageEnvFile: readEnv('CLI_E2E_LOADED_IMAGE_ENV_FILE', repoRoot.path('testresults/cli-e2e-images.env'))
    };
}
