import { describe, expect, it, vi } from 'vitest';
import { publishAsDenoDockerFile } from '../../../playground/TsIntegrationSpike/deno-integration/integration.js';

describe('Deno Dockerfile publishing', () => {
    function createResource(publish: boolean, permissionsConfigured: boolean) {
        const container = {
            withEnvironment: vi.fn(),
            withCertificateTrustEnvironment: vi.fn(),
            withSerializedAnnotation: vi.fn(),
            withDockerfile: vi.fn(),
        };
        const resource: Partial<Parameters<typeof publishAsDenoDockerFile>[0]['resource']> = {
            getSerializedAnnotation: async () => JSON.stringify({
                appHostDirectory: process.cwd(),
                appDirectory: 'deno-app',
                scriptPath: 'main.ts',
                permissionsConfigured,
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
});
