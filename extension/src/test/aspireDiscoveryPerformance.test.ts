import * as assert from 'assert';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { EventEmitter } from 'events';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspirePackageRestoreProvider } from '../utils/AspirePackageRestoreProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliProcessModule from '../utils/process/cliProcess';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import type { CapabilityStatus } from '../types/configInfo';
import { removeDirectorySafely } from './testHelpers';

/**
 * "At scale" companion to workspace.test.ts's findAspireConfigFiles() regression tests (Theme A of
 * the auto-restore hardening plan for issue #19903 / PR #19904). Those tests prove discovery is
 * bounded to a single glob walk in isolation; they do not prove the guarantee still holds once
 * hundreds of real AppHosts are flowing through the full discovery -> gate -> restore pipeline in
 * AspirePackageRestoreProvider._restoreAll(). This suite drives _restoreAll() directly (not
 * findAspireConfigFiles() in isolation) over a synthetic large workspace, so a future change that
 * reintroduces a per-config or paginated findFiles call -- or an accidental O(N^2)/serialized-await
 * regression in the per-config gating loop -- fails loudly here instead of only showing up as slow
 * extension activation in the field.
 */
suite('Aspire discovery performance', () => {
    let sandbox: sinon.SinonSandbox;

    setup(() => {
        sandbox = sinon.createSandbox();
    });

    teardown(() => sandbox.restore());

    test('discovery and restore gating stay bounded for a large synthetic workspace', async function () {
        // Real fs + stubbed-process-spawn work for hundreds of configs is normally well under a
        // second, but give slower CI hardware plenty of headroom before Mocha's own test timeout.
        this.timeout(30_000);

        const configCount = 200;
        const directories = Array.from({ length: configCount }, () =>
            fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-perf-')));

        try {
            const configUris = directories.map(directory => createStaleGuestConfig(directory));
            // getWorkspaceFolder deliberately isn't stubbed: none of these mkdtemp paths belong to
            // any real workspace folder open in the test host, so the real implementation already
            // returns undefined for all of them and getCliPathTargetForUri falls back to the
            // window-scoped target -- which is exactly what a config outside every open folder
            // should resolve to, and one fewer per-URI stub to maintain at this scale.
            sandbox.stub(vscode.workspace, 'getConfiguration').returns({
                get: <T>() => true as T,
            } as unknown as vscode.WorkspaceConfiguration);
            const findFilesStub = sandbox.stub(vscode.workspace, 'findFiles').resolves(configUris);

            const getAspireCliExecutablePath = sandbox.stub().resolves('/repo/workspace/bin/aspire');
            const provider = new AspirePackageRestoreProvider(
                { getAspireCliExecutablePath } as unknown as AspireTerminalProvider,
                createConfigInfoProvider('supported', '13.6.0'));
            const spawnStub = sandbox.stub(cliProcessModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
                const childProcess = createChildProcess();
                queueMicrotask(() => {
                    options?.exitCallback?.(0);
                    childProcess.emit('close', 0);
                });
                return childProcess as unknown as ChildProcessWithoutNullStreams;
            });

            const startMs = Date.now();
            await (provider as any)._restoreAll(false);
            const elapsedMs = Date.now() - startMs;
            provider.dispose();

            // The direct, durable regression guard: automatic restore must still perform exactly
            // the single glob walk findAspireConfigFiles() itself performs (see
            // workspace.test.ts), regardless of how many configs that walk returns. A future
            // change that pages results or globs per-directory would still pass a smaller
            // functional test but would fail this one.
            assert.strictEqual(findFilesStub.callCount, 1,
                'automatic restore should perform exactly one aspire.config.json glob walk regardless of workspace size');
            assert.strictEqual(spawnStub.callCount, configCount,
                `expected all ${configCount} stale-marker configs to be restored`);
            // Not a tight benchmark: the generous ceiling exists only to catch an accidental
            // O(N^2) or serialized-await regression in the per-config gating loop. Real hardware
            // completes this in well under a second; approaching this ceiling would mean the
            // _maxConcurrency-bounded batching in _restoreAll degraded to effectively sequential.
            assert.ok(elapsedMs < 15_000,
                `expected discovery+gating for ${configCount} configs to complete in well under 15s, took ${elapsedMs}ms`);
        } finally {
            for (const directory of directories) {
                removeDirectorySafely(directory);
            }
        }
    });
});

function createChildProcess(): EventEmitter & { kill: sinon.SinonStub } {
    return Object.assign(new EventEmitter(), { kill: sinon.stub() });
}

/**
 * Writes a minimal non-.NET (TypeScript) AppHost config whose generated modules carry a stale
 * .codegen-version marker, so _getAutoRestoreCli's full classify -> locate-modules -> resolve-CLI
 * -> compare-version path runs to completion (and reports "restore needed") for every config
 * instead of short-circuiting early -- this is the worst case for the gating loop's cost, which is
 * what this suite exists to bound.
 */
function createStaleGuestConfig(directory: string): vscode.Uri {
    const configUri = vscode.Uri.file(path.join(directory, 'aspire.config.json'));
    fs.writeFileSync(configUri.fsPath, JSON.stringify({
        appHost: {
            path: 'apphost.mts',
            language: 'typescript/nodejs',
        },
    }));

    const modulesDirectory = path.join(directory, '.aspire', 'modules');
    fs.mkdirSync(modulesDirectory, { recursive: true });
    fs.writeFileSync(path.join(modulesDirectory, '.codegen-version'), '13.0.0-old');

    return configUri;
}

function createConfigInfoProvider(status: CapabilityStatus, version: string): ConfigInfoProvider {
    return {
        getCapabilityStatus: sinon.stub().resolves(status),
        getCliVersion: sinon.stub().resolves({
            cliPath: '/repo/workspace/bin/aspire',
            version,
            executableIdentity: 'test-cli',
        }),
    } as unknown as ConfigInfoProvider;
}
