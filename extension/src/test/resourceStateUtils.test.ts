import * as assert from 'assert';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { findResourceState, findWorkspaceResourceState, matchesAppHostPathOrDirectory } from '../editor/resourceStateUtils';
import type { ResourceJson, AppHostDisplayInfo } from '../data/AppHostDataRepository';
import { ResourceState } from '../editor/resourceConstants';

function makeResource(overrides: Partial<ResourceJson> = {}): ResourceJson {
    return {
        name: 'my-service',
        displayName: null,
        resourceType: 'Project',
        state: null,
        stateStyle: null,
        healthStatus: null,
        dashboardUrl: null,
        urls: null,
        commands: null,
        properties: null,
        ...overrides,
    } as ResourceJson;
}

function makeAppHost(overrides: Partial<AppHostDisplayInfo> = {}): AppHostDisplayInfo {
    return {
        appHostPath: '/test/AppHost.csproj',
        appHostPid: 1234,
        cliPid: null,
        dashboardUrl: null,
        resources: null,
        ...overrides,
    } as AppHostDisplayInfo;
}

function createSymlinkedWorkspace(): {
    canonicalDirectory: string;
    linkedDirectory: string;
    dispose(): void;
} {
    const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-resource-state-'));
    const canonicalDirectory = path.join(tempDirectory, 'workspace');
    const linkedDirectory = path.join(tempDirectory, 'workspace-link');

    fs.mkdirSync(canonicalDirectory);
    fs.symlinkSync(canonicalDirectory, linkedDirectory, process.platform === 'win32' ? 'junction' : 'dir');

    return {
        canonicalDirectory,
        linkedDirectory,
        dispose() {
            fs.rmSync(tempDirectory, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
        },
    };
}

suite('findResourceState', () => {
    test('returns undefined when no appHosts provided', () => {
        const result = findResourceState([], 'cache');
        assert.strictEqual(result, undefined);
    });

    test('returns undefined when appHosts have no resources', () => {
        const appHost = makeAppHost({ resources: null });
        const result = findResourceState([appHost], 'cache');
        assert.strictEqual(result, undefined);
    });

    test('returns undefined when appHosts have empty resources', () => {
        const appHost = makeAppHost({ resources: [] });
        const result = findResourceState([appHost], 'cache');
        assert.strictEqual(result, undefined);
    });

    test('matches resource by name', () => {
        const resource = makeResource({ name: 'cache', displayName: null });
        const appHost = makeAppHost({ resources: [resource] });
        const result = findResourceState([appHost], 'cache');
        assert.ok(result);
        assert.strictEqual(result!.resource, resource);
        assert.strictEqual(result!.appHost, appHost);
    });

    test('matches resource by displayName', () => {
        const resource = makeResource({ name: 'cache-abc123', displayName: 'cache' });
        const appHost = makeAppHost({ resources: [resource] });
        const result = findResourceState([appHost], 'cache');
        assert.ok(result);
        assert.strictEqual(result!.resource, resource);
    });

    test('prefers displayName match over name match', () => {
        const resource = makeResource({ name: 'cache-abc123', displayName: 'cache' });
        const appHost = makeAppHost({ resources: [resource] });
        // Searching by displayName should find it
        const result = findResourceState([appHost], 'cache');
        assert.ok(result);
        assert.strictEqual(result!.resource.displayName, 'cache');
    });

    test('returns undefined when resource name does not match', () => {
        const resource = makeResource({ name: 'redis', displayName: 'redis' });
        const appHost = makeAppHost({ resources: [resource] });
        const result = findResourceState([appHost], 'cache');
        assert.strictEqual(result, undefined);
    });

    test('searches across multiple appHosts', () => {
        const resource1 = makeResource({ name: 'cache', displayName: 'cache' });
        const resource2 = makeResource({ name: 'db', displayName: 'db' });
        const appHost1 = makeAppHost({ appHostPid: 1, resources: [resource1] });
        const appHost2 = makeAppHost({ appHostPid: 2, resources: [resource2] });

        const result = findResourceState([appHost1, appHost2], 'db');
        assert.ok(result);
        assert.strictEqual(result!.resource, resource2);
        assert.strictEqual(result!.appHost, appHost2);
    });

    test('returns first match when name exists in multiple appHosts', () => {
        const resource1 = makeResource({ name: 'cache', state: ResourceState.Running });
        const resource2 = makeResource({ name: 'cache', state: ResourceState.Stopped });
        const appHost1 = makeAppHost({ appHostPid: 1, resources: [resource1] });
        const appHost2 = makeAppHost({ appHostPid: 2, resources: [resource2] });

        const result = findResourceState([appHost1, appHost2], 'cache');
        assert.ok(result);
        assert.strictEqual(result!.resource.state, ResourceState.Running);
    });

    test('skips appHosts with null resources', () => {
        const resource = makeResource({ name: 'cache' });
        const appHost1 = makeAppHost({ appHostPid: 1, resources: null });
        const appHost2 = makeAppHost({ appHostPid: 2, resources: [resource] });

        const result = findResourceState([appHost1, appHost2], 'cache');
        assert.ok(result);
        assert.strictEqual(result!.appHost, appHost2);
    });
});

suite('findWorkspaceResourceState', () => {
    test('returns undefined when no workspace resources', () => {
        const finder = findWorkspaceResourceState([], '/test/path');
        const result = finder('cache');
        assert.strictEqual(result, undefined);
    });

    test('matches resource by name', () => {
        const resource = makeResource({ name: 'cache' });
        const finder = findWorkspaceResourceState([resource], '/test/AppHost.csproj');
        const result = finder('cache');
        assert.ok(result);
        assert.strictEqual(result!.resource, resource);
        assert.strictEqual(result!.appHost.appHostPath, '/test/AppHost.csproj');
    });

    test('matches resource by displayName', () => {
        const resource = makeResource({ name: 'cache-xyz', displayName: 'cache' });
        const finder = findWorkspaceResourceState([resource], '/test/AppHost.csproj');
        const result = finder('cache');
        assert.ok(result);
        assert.strictEqual(result!.resource, resource);
    });

    test('returns undefined when no match found', () => {
        const resource = makeResource({ name: 'db' });
        const finder = findWorkspaceResourceState([resource], '/test/AppHost.csproj');
        const result = finder('cache');
        assert.strictEqual(result, undefined);
    });

    test('returned appHost has expected default values', () => {
        const resource = makeResource({ name: 'cache' });
        const finder = findWorkspaceResourceState([resource], '/test/AppHost.csproj');
        const result = finder('cache');
        assert.ok(result);
        assert.strictEqual(result!.appHost.appHostPid, 0);
        assert.strictEqual(result!.appHost.cliPid, null);
        assert.strictEqual(result!.appHost.dashboardUrl, null);
    });

    test('returned appHost resources is a copy', () => {
        const resource = makeResource({ name: 'cache' });
        const resources = [resource];
        const finder = findWorkspaceResourceState(resources, '/test/AppHost.csproj');
        const result = finder('cache');
        assert.ok(result);
        assert.notStrictEqual(result!.appHost.resources, resources);
        assert.deepStrictEqual(result!.appHost.resources, resources);
    });

    test('can be called multiple times with different names', () => {
        const r1 = makeResource({ name: 'cache' });
        const r2 = makeResource({ name: 'db' });
        const finder = findWorkspaceResourceState([r1, r2], '/test/AppHost.csproj');
        assert.ok(finder('cache'));
        assert.ok(finder('db'));
        assert.strictEqual(finder('nonexistent'), undefined);
    });
});

suite('matchesAppHostPathOrDirectory', () => {
    test('preserves normalized matching when paths no longer exist', () => {
        const documentPath = path.join(path.sep, 'missing', 'AppHost', 'AppHost.cs');
        const appHostPath = path.join(path.sep, 'missing', 'AppHost', 'AppHost.csproj');

        assert.strictEqual(matchesAppHostPathOrDirectory(documentPath, appHostPath), true);
    });

    test('does not throw when mismatched paths no longer exist', () => {
        const documentPath = path.join(path.sep, 'missing', 'FirstAppHost', 'AppHost.cs');
        const appHostPath = path.join(path.sep, 'missing', 'SecondAppHost', 'AppHost.csproj');

        assert.strictEqual(matchesAppHostPathOrDirectory(documentPath, appHostPath), false);
    });

    test('does not throw when canonicalization encounters a symlink loop', () => {
        const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-resource-state-loop-'));
        const loopPath = path.join(tempDirectory, 'loop');
        const appHostDirectory = path.join(tempDirectory, 'apphost');
        const appHostPath = path.join(appHostDirectory, 'AppHost.csproj');
        let loopCreated = false;
        try {
            fs.mkdirSync(appHostDirectory);
            fs.writeFileSync(appHostPath, '');
            fs.symlinkSync(loopPath, loopPath, process.platform === 'win32' ? 'junction' : 'dir');
            loopCreated = true;

            assert.strictEqual(matchesAppHostPathOrDirectory(path.join(loopPath, 'AppHost.cs'), appHostPath), false);
        } finally {
            if (loopCreated) {
                fs.unlinkSync(loopPath);
            }
            fs.rmSync(tempDirectory, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
        }
    });

    test('matches a TypeScript AppHost opened through a directory symlink', () => {
        const workspace = createSymlinkedWorkspace();
        try {
            const canonicalAppHostPath = path.join(workspace.canonicalDirectory, 'apphost.ts');
            const linkedAppHostPath = path.join(workspace.linkedDirectory, 'apphost.ts');
            fs.writeFileSync(canonicalAppHostPath, '');

            assert.strictEqual(matchesAppHostPathOrDirectory(linkedAppHostPath, canonicalAppHostPath), true);
        } finally {
            workspace.dispose();
        }
    });

    test('matches a C# source file to its project through a directory symlink', () => {
        const workspace = createSymlinkedWorkspace();
        try {
            const canonicalProjectPath = path.join(workspace.canonicalDirectory, 'AppHost.csproj');
            const linkedSourcePath = path.join(workspace.linkedDirectory, 'AppHost.cs');
            fs.writeFileSync(canonicalProjectPath, '');
            fs.writeFileSync(path.join(workspace.canonicalDirectory, 'AppHost.cs'), '');

            assert.strictEqual(matchesAppHostPathOrDirectory(linkedSourcePath, canonicalProjectPath), true);
        } finally {
            workspace.dispose();
        }
    });

    test('does not match unrelated canonical directories', () => {
        const firstWorkspace = createSymlinkedWorkspace();
        const secondWorkspace = createSymlinkedWorkspace();
        try {
            const documentPath = path.join(firstWorkspace.linkedDirectory, 'apphost.ts');
            const appHostPath = path.join(secondWorkspace.canonicalDirectory, 'apphost.ts');
            fs.writeFileSync(path.join(firstWorkspace.canonicalDirectory, 'apphost.ts'), '');
            fs.writeFileSync(appHostPath, '');

            assert.strictEqual(matchesAppHostPathOrDirectory(documentPath, appHostPath), false);
        } finally {
            firstWorkspace.dispose();
            secondWorkspace.dispose();
        }
    });
});
