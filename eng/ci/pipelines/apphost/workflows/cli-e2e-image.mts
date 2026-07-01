import type { DistributedApplicationPipelinePromise } from '../.aspire/modules/aspire.mjs';
import {
    buildDockerImageWithAttempts,
    defaultUbuntuAptMirror,
    ensureDockerBuildxBuilder,
    saveDockerImageTarball,
    tagDockerImage,
    verifyDocker
} from '../lib/docker.mjs';
import type { DockerImageBuildAttempt } from '../lib/docker.mjs';
import { readBooleanEnv, readEnv } from '../lib/env.mjs';
import type { RepoRoot } from '../lib/repo.mjs';

// Shadows .github/workflows/build-cli-e2e-image.yml.
const dotnetImageArtifact = 'aspire-cli-e2e-dotnet.tar.gz';
const polyglotImageArtifact = 'aspire-cli-e2e-polyglot.tar.gz';
const polyglotJavaImageArtifact = 'aspire-cli-e2e-polyglot-java.tar.gz';

interface CliE2EImageSettings {
    includePolyglotImages: boolean;
    saveImageTarballs: boolean;
    dotnetImageTag: string;
    dotnetImageCacheScope: string;
    polyglotImageTag: string;
    polyglotBaseImageTag: string;
    polyglotBaseImageCacheScope: string;
    polyglotJavaImageTag: string;
    ubuntuAptMirror: string;
}

export function addCliE2EImageWorkflow(
    pipeline: DistributedApplicationPipelinePromise,
    repoRoot: RepoRoot): DistributedApplicationPipelinePromise {
    return pipeline
        .addStep('ci-cli-e2e-image', async () => { })
        .addStep('cli-e2e-verify-docker', async context => {
            await verifyDocker(context, repoRoot);
        }, {
            requiredBy: ['ci-cli-e2e-image']
        })
        .addStep('cli-e2e-build-images', async context => {
            const settings = getCliE2EImageSettings();
            const buildAttempts = getCliE2EImageBuildAttempts(settings);

            await ensureDockerBuildxBuilder(context, repoRoot, 'cli-e2e-builder');

            await buildDockerImageWithAttempts(context, repoRoot, {
                displayName: '.NET CLI E2E image',
                dockerfile: 'tests/Shared/Docker/Dockerfile.e2e',
                tag: settings.dotnetImageTag,
                cacheScope: settings.dotnetImageCacheScope,
                buildArgs: {
                    SKIP_SOURCE_BUILD: 'true'
                }
            }, buildAttempts);

            if (!settings.includePolyglotImages) {
                const reportingStep = await context.reportingStep();
                await reportingStep.logStep('information', 'Skipping polyglot CLI E2E images because CLI_E2E_INCLUDE_POLYGLOT_IMAGES=false.');
                return;
            }

            await buildDockerImageWithAttempts(context, repoRoot, {
                displayName: 'polyglot base CLI E2E image',
                dockerfile: 'tests/Shared/Docker/Dockerfile.e2e-polyglot-base',
                tag: settings.polyglotBaseImageTag,
                cacheScope: settings.polyglotBaseImageCacheScope,
                buildArgs: {
                    SKIP_SOURCE_BUILD: 'true'
                }
            }, buildAttempts);
            await tagDockerImage(context, repoRoot, settings.polyglotBaseImageTag, settings.polyglotImageTag);
            await buildDockerImageWithAttempts(context, repoRoot, {
                displayName: 'Java polyglot CLI E2E image',
                dockerfile: 'tests/Shared/Docker/Dockerfile.e2e-polyglot-java',
                tag: settings.polyglotJavaImageTag,
                useBuildx: false,
                env: {
                    DOCKER_BUILDKIT: '1'
                }
            }, buildAttempts);
        }, {
            dependsOn: ['cli-e2e-verify-docker'],
            requiredBy: ['ci-cli-e2e-image']
        })
        .addStep('cli-e2e-save-image-tarballs', async context => {
            const settings = getCliE2EImageSettings();

            if (!settings.saveImageTarballs) {
                const reportingStep = await context.reportingStep();
                await reportingStep.logStep('information', 'Skipping CLI E2E image tarball save because CLI_E2E_SAVE_IMAGE_TARBALLS=false.');
                return;
            }

            await saveDockerImageTarball(context, repoRoot, '.NET CLI E2E image', settings.dotnetImageTag, repoRoot.path('artifacts/cli-e2e-image', dotnetImageArtifact));

            if (!settings.includePolyglotImages) {
                return;
            }

            await saveDockerImageTarball(context, repoRoot, 'polyglot CLI E2E image', settings.polyglotImageTag, repoRoot.path('artifacts/cli-e2e-image', polyglotImageArtifact));
            await saveDockerImageTarball(context, repoRoot, 'Java polyglot CLI E2E image', settings.polyglotJavaImageTag, repoRoot.path('artifacts/cli-e2e-image', polyglotJavaImageArtifact));
        }, {
            dependsOn: ['cli-e2e-build-images'],
            requiredBy: ['ci-cli-e2e-image']
        });
}

function getCliE2EImageSettings(): CliE2EImageSettings {
    return {
        includePolyglotImages: readBooleanEnv('CLI_E2E_INCLUDE_POLYGLOT_IMAGES', true),
        saveImageTarballs: readBooleanEnv('CLI_E2E_SAVE_IMAGE_TARBALLS', true),
        dotnetImageTag: readEnv('CLI_E2E_DOTNET_IMAGE_TAG', 'aspire-cli-e2e-dotnet:prebuilt'),
        dotnetImageCacheScope: readEnv('CLI_E2E_DOTNET_IMAGE_CACHE_SCOPE', 'cli-e2e-dotnet'),
        polyglotImageTag: readEnv('CLI_E2E_POLYGLOT_IMAGE_TAG', 'aspire-cli-e2e-polyglot:prebuilt'),
        polyglotBaseImageTag: readEnv('CLI_E2E_POLYGLOT_BASE_IMAGE_TAG', 'aspire-e2e-polyglot-base:latest'),
        polyglotBaseImageCacheScope: readEnv('CLI_E2E_POLYGLOT_BASE_IMAGE_CACHE_SCOPE', 'cli-e2e-polyglot-base'),
        polyglotJavaImageTag: readEnv('CLI_E2E_POLYGLOT_JAVA_IMAGE_TAG', 'aspire-cli-e2e-polyglot-java:prebuilt'),
        ubuntuAptMirror: readEnv('CI_UBUNTU_APT_MIRROR', defaultUbuntuAptMirror)
    };
}

function getCliE2EImageBuildAttempts(settings: CliE2EImageSettings): DockerImageBuildAttempt[] {
    return [
        {
            name: 'configured Ubuntu apt mirror',
            buildArgs: {
                UBUNTU_APT_MIRROR: settings.ubuntuAptMirror
            }
        },
        {
            name: 'default Ubuntu apt sources'
        }
    ];
}
