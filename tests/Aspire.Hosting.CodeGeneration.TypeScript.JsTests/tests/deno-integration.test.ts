import { readFileSync } from 'node:fs';
import { describe, expect, it, vi } from 'vitest';
import { publishAsDenoDockerFile } from '../../../playground/TsIntegrationSpike/deno-integration/integration.js';
import type { SerializedAnnotationStore } from '../../../playground/TsIntegrationSpike/deno-integration/annotations.js';

describe('Deno Dockerfile publishing', () => {
    function createStage() {
        return {
            workDir: vi.fn().mockReturnThis(),
            copy: vi.fn().mockReturnThis(),
            copyFrom: vi.fn().mockReturnThis(),
            run: vi.fn().mockReturnThis(),
            env: vi.fn().mockReturnThis(),
            expose: vi.fn().mockReturnThis(),
            user: vi.fn().mockReturnThis(),
            entrypoint: vi.fn().mockReturnThis(),
            addContainerFiles: vi.fn().mockReturnThis(),
        };
    }

    function createResource(publish: boolean, permissionsConfigured: boolean, state: {
        scriptPath?: string;
        permissions?: string[];
        args?: string[];
        buildTask?: string;
        buildTaskArgs?: string[];
        runTask?: string;
        runTaskArgs?: string[];
    } = {}) {
        const stages = { build: createStage(), runtime: createStage() };
        const dockerfile = {
            addContainerFilesStages: vi.fn(),
            from: vi.fn(async (_image: string, options: { stageName: keyof typeof stages }) => stages[options.stageName]),
        };
        let containerState = '';
        const container = {
            withEnvironment: vi.fn(),
            withCertificateTrustEnvironment: vi.fn(),
            withSerializedAnnotation: vi.fn(async (_id: string, json: string) => { containerState = json; }),
            getSerializedAnnotation: async () => containerState,
            hasSerializedAnnotation: async () => containerState.length > 0,
            withDockerfile: vi.fn(),
            withDockerfileBuilder: vi.fn(async (_directory: string, configure: (context: {
                builder(): Promise<typeof dockerfile>;
                resource(): Promise<SerializedAnnotationStore>;
            }) => Promise<void>) => {
                await configure({
                    builder: async () => dockerfile,
                    resource: async () => container,
                });
            }),
        };
        const resource: Partial<Parameters<typeof publishAsDenoDockerFile>[0]['resource']> = {
            getSerializedAnnotation: async () => JSON.stringify({
                appHostDirectory: process.cwd(),
                appDirectory: 'deno-app',
                scriptPath: 'main.ts',
                permissionsConfigured,
                ...state,
            }),
            publishAsDockerFile: vi.fn().mockImplementation(async (configure: (value: typeof container) => Promise<void>) => {
                if (publish) {
                    await configure(container);
                }
            }),
        };
        return {
            resource: resource as Parameters<typeof publishAsDenoDockerFile>[0]['resource'],
            container,
            dockerfile,
            stages,
        };
    }

    it('does not reject publish-only conflicts in run mode', async () => {
        const { resource, container } = createResource(false, true);

        await expect(publishAsDenoDockerFile({
            resource,
            useExistingDockerfile: true,
            runtimeImage: 'custom-deno',
        })).resolves.toBe(resource);
        expect(container.withDockerfile).not.toHaveBeenCalled();
    });

    it('rejects unsupported options before modifying a publish container', async () => {
        const { resource, container } = createResource(true, true);

        await expect(publishAsDenoDockerFile({
            resource,
            useExistingDockerfile: true,
        })).rejects.toThrow('Aspire cannot apply Deno permissions');
        expect(container.withEnvironment).not.toHaveBeenCalled();
    });

    it('adopts an existing Dockerfile when there are no conflicting options', async () => {
        const { resource, container } = createResource(true, false);

        await publishAsDenoDockerFile({ resource, useExistingDockerfile: true });

        expect(container.withDockerfile).toHaveBeenCalledExactlyOnceWith('deno-app', {
            dockerfilePath: 'Dockerfile',
            stage: undefined,
        });
    });

    it.each([undefined, true])('caches without runtime permissions when cache is %s', async (cache) => {
        const { resource, container, dockerfile, stages } = createResource(true, false, {
            buildTask: 'check',
        });

        await publishAsDenoDockerFile({ resource, cache, port: 8000, useExistingDockerfile: false });

        expect(dockerfile.from.mock.calls).toEqual([
            ['denoland/deno:alpine-2.5.6', { stageName: 'build' }],
            ['denoland/deno:alpine-2.5.6', { stageName: 'runtime' }],
        ]);
        expect(stages.build.run.mock.calls).toEqual([
            ['deno cache main.ts'],
            ['deno task check'],
        ]);
        expect(stages.runtime.entrypoint.mock.calls).toEqual([
            [['deno', 'run', '--allow-net', '--allow-env', 'main.ts']],
        ]);
        expect(stages.runtime.expose).toHaveBeenCalledExactlyOnceWith(8000);
        expect(stages.runtime.user).toHaveBeenCalledExactlyOnceWith('deno');
        expect(dockerfile.addContainerFilesStages).toHaveBeenCalledExactlyOnceWith(container);
        expect(stages.runtime.addContainerFiles).toHaveBeenCalledExactlyOnceWith(container, '/app');
    });

    it('skips only caching when cache is false', async () => {
        const { resource, stages } = createResource(true, false, {
            buildTask: 'check',
            buildTaskArgs: ['--all'],
            runTask: 'serve',
            runTaskArgs: ['--port', '8000'],
        });

        await publishAsDenoDockerFile({ resource, cache: false, useExistingDockerfile: false });

        expect(stages.build.run.mock.calls).toEqual([['deno task check --all']]);
        expect(stages.runtime.entrypoint.mock.calls).toEqual([
            [['deno', 'task', 'serve', '--port', '8000']],
        ]);
    });

    it('quotes cache and build arguments while preserving direct runtime permissions and arguments', async () => {
        const scriptPath = "src/it's main.ts";
        const { resource, stages } = createResource(true, true, {
            scriptPath,
            permissions: ['--allow-net=example.com', '--allow-read=./data files'],
            args: ['hello world', '$HOME'],
            buildTask: 'check',
            buildTaskArgs: ['--stale'],
        });

        await publishAsDenoDockerFile({
            resource,
            buildTask: 'build release',
            buildArgs: ["it's ready", '$HOME; echo injected', ''],
            useExistingDockerfile: false,
        });

        expect(stages.build.run.mock.calls).toEqual([
            ["deno cache 'src/it'\\''s main.ts'"],
            ["deno task 'build release' 'it'\\''s ready' '$HOME; echo injected' ''"],
        ]);
        expect(stages.runtime.entrypoint.mock.calls).toEqual([
            [['deno', 'run', '--allow-net=example.com', '--allow-read=./data files', scriptPath, 'hello world', '$HOME']],
        ]);
    });

    it('keeps the standalone cache task separate from runtime permissions', () => {
        const config = JSON.parse(readFileSync(
            new URL('../../../playground/TsIntegrationSpike/deno-api/deno.json', import.meta.url),
            'utf8'));

        expect(config.tasks).toEqual({
            serve: 'deno run --allow-net --allow-env main.ts',
            check: 'deno check main.ts',
            cache: 'deno cache main.ts',
        });
    });
});
