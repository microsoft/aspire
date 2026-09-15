import * as assert from 'assert';
import { spawn } from 'child_process';
import { once } from 'events';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { createInterface } from 'readline';
import * as ts from 'typescript';
import * as vm from 'vm';

const runnerPath = path.resolve(__dirname, '..', '..', 'scripts', 'run-e2e.js');
const runnerSource = ts.createSourceFile(runnerPath, fs.readFileSync(runnerPath, 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
const runnerFunctions = runnerSource.statements.filter(ts.isFunctionDeclaration).map(declaration => declaration.getText(runnerSource)).join('\n');
const representativeDiagnostics = {
    'aspire-home/logs/cli.log': 'CLI completed.\n',
    'aspire-home/cache/apphost-info/app.json': '{"status":"stopped"}\n',
    'aspire-home/aspire.config.json': '{"channel":"daily"}\n',
    'settings/logs/session/window/Aspire Extension.log': 'Extension stopped.\n',
    'settings/User/settings.json': '{"aspire.viewMode":"workspace"}\n',
    'screenshots/failure.png': 'screenshot bytes',
};
const representativeWorkspaceDiagnostics = {
    'aspire.config.json': '{"appHost":{"path":"AspireE2E.AppHost/AppHost.cs"}}\n',
    '.aspire/settings.json': '{"appHostPath":"AspireE2E.AppHost/AppHost.cs"}\n',
    '.aspire/logs/apphost.log': 'AppHost stopped.\n',
    '.vscode/settings.json': '{"aspire.viewMode":"workspace"}\n',
    'AspireE2E.AppHost/AppHost.cs': 'var builder = DistributedApplication.CreateBuilder(args);\n',
    'AspireE2E.AppHost/Properties/launchSettings.json': '{"profiles":{}}\n',
    'AspireE2E.AppHost/AspireE2E.AppHost.csproj': '<Project Sdk="Aspire.AppHost.Sdk" />\n',
    'AspireE2E.WinUI/App.xaml': '<Application />\n',
    'AspireE2E.WinUI/App.xaml.cs': 'partial class App { }\n',
    'AspireE2E.WinUI/Program.cs': 'Application.Start(_ => new App());\n',
    'AspireE2E.WinUI/app.manifest': '<assembly />\n',
    'winui-e2e-ready.txt': 'ready\n',
};

interface CollectionHooks {
    beforeCopy?: (sourcePath: string) => void;
    beforeRead?: (filePath: string) => void;
}

function writeFiles(root: string, files: Record<string, string>): void {
    for (const [relativePath, contents] of Object.entries(files)) {
        const filePath = path.join(root, ...relativePath.split('/'));
        fs.mkdirSync(path.dirname(filePath), { recursive: true });
        fs.writeFileSync(filePath, contents);
    }
}

function readFiles(root: string): Record<string, string> {
    return Object.fromEntries(fs.readdirSync(root, { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile())
        .map(entry => {
            const filePath = path.join(entry.parentPath, entry.name);
            return [path.relative(root, filePath).split(path.sep).join('/'), fs.readFileSync(filePath, 'utf8')];
        }));
}

async function collectDiagnostics(root: string, hooks: CollectionHooks = {}, workspace = false): Promise<void> {
    const exports: { collect?: () => Promise<void> | void } = {};
    // Execute the real collector and its helpers without running module setup or main(), which
    // allocate an E2E workspace and launch VS Code. Other runner contract tests use the same AST/VM pattern.
    vm.runInNewContext(`${runnerFunctions}\nexports.collect = ${workspace ? 'copyWorkspaceDiagnostics' : 'copyStorageDiagnostics'};`, {
        exports,
        path,
        process,
        Error,
        AggregateError,
        isWindows: process.platform === 'win32',
        isolatedAspireHome: path.join(root, 'aspire-home'),
        storageDir: path.join(root, 'storage'),
        storageDiagnosticsDir: path.join(root, 'diagnostics'),
        workspaceRoot: path.join(root, 'workspace'),
        workspaceDiagnosticsDir: path.join(root, 'workspace-diagnostics'),
        winUiReadyMarkerPath: path.join(root, 'workspace', 'winui-e2e-ready.txt'),
        fs: {
            ...fs,
            cpSync(sourcePath: string | URL, destinationPath: string | URL, options?: fs.CopySyncOptions) {
                const filter = options?.filter;
                // Keep real filesystem operations, including failures. This hook changes source files
                // after enumeration, without racing another test thread. Cover both the old cpSync
                // collector and the per-file collector with the same before/after regression suite.
                fs.cpSync(sourcePath, destinationPath, hooks.beforeCopy ? {
                    ...options,
                    filter(source, destination) {
                        hooks.beforeCopy?.(source);
                        return filter ? filter(source, destination) : true;
                    },
                } : options);
            },
            lstatSync(filePath: string) {
                hooks.beforeCopy?.(filePath);
                return fs.lstatSync(filePath);
            },
            readFileSync(filePath: string, encoding?: BufferEncoding) {
                hooks.beforeRead?.(filePath);
                return encoding ? fs.readFileSync(filePath, encoding) : fs.readFileSync(filePath);
            },
        },
    });
    assert.ok(exports.collect);
    await exports.collect();
}

async function withExclusiveFileLock(filePath: string, action: () => Promise<void>): Promise<void> {
    // Node's open flags cannot request Windows FileShare.None. Match Shared/FileLock.cs, including
    // DeleteOnClose, in an owned child that releases its handle when the parent closes stdin.
    const holder = spawn('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', `
        $ErrorActionPreference = 'Stop'
        $lock = [System.IO.FileStream]::new(
            $env:ASPIRE_EXTENSION_TEST_LOCK_PATH,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None,
            1,
            [System.IO.FileOptions]::DeleteOnClose)
        try {
            [Console]::Out.WriteLine('locked')
            [Console]::Out.Flush()
            [Console]::In.ReadLine() | Out-Null
        }
        finally {
            $lock.Dispose()
        }
    `], {
        env: { ...process.env, ASPIRE_EXTENSION_TEST_LOCK_PATH: filePath },
        windowsHide: true,
        timeout: 15000,
    });
    const output = createInterface({ input: holder.stdout });
    let stderr = '';
    holder.stderr.setEncoding('utf8').on('data', chunk => { stderr += chunk; });
    const closed = once(holder, 'close');

    try {
        const [line] = await Promise.race([
            once(output, 'line'),
            closed.then(([code, signal]) => {
                throw new Error(`Lock holder exited before acquiring the file: code=${code}, signal=${signal}, ${stderr}`);
            }),
        ]);
        assert.strictEqual(line, 'locked');
        await action();
    }
    finally {
        holder.stdin.end();
        output.close();
        const [code, signal] = await closed;
        assert.strictEqual(code, 0, `Lock holder failed: signal=${signal}, ${stderr}`);
    }
}

suite('E2E storage diagnostics', () => {
    let root: string;

    setup(() => {
        root = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-e2e-storage-diagnostics-'));
        writeFiles(root, Object.fromEntries(Object.entries(representativeDiagnostics)
            .map(([relativePath, contents]) => [relativePath.startsWith('aspire-home/') ? relativePath : `storage/${relativePath}`, contents])));
    });

    teardown(() => {
        fs.rmSync(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    });

    test('collects only intended storage diagnostics and redacts them', async () => {
        const retainedFiles = {
            'aspire-home/logs/dashboard/session.log': 'Dashboard diagnostics outside persistence.\n',
            'aspire-home/cache/apphost-info/state.lock': 'An unrelated lock-named diagnostic.\n',
            'aspire-home/logs/.leases-info.log': 'Not a lease directory.\n',
        };
        writeFiles(root, {
            ...retainedFiles,
            'aspire-home/logs/cli.log': 'http://localhost:1234/login?t=private-dashboard-token\n',
            'aspire-home/dashboard/runs/20260914T045039914Z.lock': '',
            'aspire-home/dashboard/runs/20260914T045039914Z/run.json': '{"runId":"20260914T045039914Z"}',
            'aspire-home/dashboard/runs/20260914T045039914Z/dashboard.db': 'run database',
            'aspire-home/dashboard/resumes/app.lock': '',
            'aspire-home/dashboard/resumes/app/dashboard.db-wal': 'live WAL',
            'aspire-home/dashboard-backup/info.json': 'Not a selected diagnostic.\n',
            'aspire-home/cache/workspace-config-locks/workspace.lock': '',
            'aspire-home/cache/skills/content.json': 'Cached runtime state.\n',
            'aspire-home/packages/.aspire-bundle-lock': '',
            'aspire-home/unrecognized-state.json': 'Not a selected diagnostic.\n',
            'aspire-home/cache/apphost-info/.leases/app.json': 'lease state',
            'aspire-home/cache/apphost-info/app.lease': '',
            'aspire-home/legacy.lease': '',
            'storage/settings/CrashpadMetrics-active.pma': 'Active runtime state.\n',
            'storage/settings/User/globalStorage/cache.json': 'Not selected VS Code settings.\n',
        });

        await collectDiagnostics(root);

        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), {
            ...representativeDiagnostics,
            ...retainedFiles,
            'aspire-home/logs/cli.log': 'http://localhost:1234/login?t=<redacted>\n',
        });
    });

    test('does not enumerate unselected runtime state as files disappear', async () => {
        writeFiles(root, {
            'aspire-home/dashboard/runs/20260914T045039914Z.lock': '',
            'aspire-home/dashboard/runs/20260914T045039914Z/run.json': '{}',
            'aspire-home/cache/workspace-config-locks/workspace.lock': '',
        });
        const visitedRuntimePaths: string[] = [];

        await collectDiagnostics(root, {
            beforeCopy(sourcePath) {
                const relativePath = path.relative(path.join(root, 'aspire-home'), sourcePath).split(path.sep).join('/');
                if (relativePath === 'logs') {
                    // DeleteOnClose and retention can remove runtime files while diagnostics are read.
                    fs.rmSync(path.join(root, 'aspire-home', 'dashboard'), { recursive: true });
                    fs.rmSync(path.join(root, 'aspire-home', 'cache', 'workspace-config-locks'), { recursive: true });
                }
                if (/^(dashboard|cache\/workspace-config-locks)(\/|$)/.test(relativePath)) {
                    visitedRuntimePaths.push(relativePath);
                }
            },
        });

        assert.deepStrictEqual(visitedRuntimePaths, []);
        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), representativeDiagnostics);
    });

    test('uses case-insensitive lease exclusions on Windows', async function () {
        if (process.platform !== 'win32') {
            this.skip();
        }
        writeFiles(root, {
            'aspire-home/DASHBOARD/runs/app.lock': '',
            'aspire-home/cache/apphost-info/.LEASES/app.json': 'lease state',
            'aspire-home/cache/apphost-info/app.LEASE': '',
        });

        await collectDiagnostics(root);

        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), representativeDiagnostics);
    });

    for (const [name, relativeLockPath] of [
        ['Dashboard runs', 'aspire-home/dashboard/runs/20260914T045039914Z.lock'],
        ['Dashboard resumes', 'aspire-home/dashboard/resumes/20260914T045039914Z.lock'],
        ['workspace configuration cache', 'aspire-home/cache/workspace-config-locks/workspace.lock'],
        ['bundle extraction', 'aspire-home/packages/.aspire-bundle-lock'],
    ]) {
        test(`collects storage diagnostics with an exclusively held ${name} lock`, async function () {
            if (process.platform !== 'win32') {
                this.skip();
            }
            writeFiles(root, { [relativeLockPath]: '' });
            const lockPath = path.join(root, ...relativeLockPath.split('/'));

            await withExclusiveFileLock(lockPath, async () => {
                // A control copy proves the OS lock is actually held, rather than just testing its name.
                assert.throws(() => fs.copyFileSync(lockPath, path.join(root, 'control.lock')), { code: 'EBUSY' });

                await collectDiagnostics(root);

                assert.strictEqual(fs.existsSync(lockPath), true);
                assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), representativeDiagnostics);
            });
            assert.strictEqual(fs.existsSync(lockPath), false, 'The lock must be deleted when its owner exits.');
        });
    }

    test('reports a locked diagnostic while retaining and redacting other files and sources', async function () {
        if (process.platform !== 'win32') {
            this.skip();
        }
        const lockPath = path.join(root, 'aspire-home', 'logs', 'unexpected.lock');
        writeFiles(root, {
            'aspire-home/logs/cli.log': 'http://localhost:1234/login?t=private-dashboard-token\n',
            'storage/settings/logs/session/window/Aspire Extension.log': 'Setting up RPC server with token: private-rpc-token\n',
        });

        await withExclusiveFileLock(lockPath, async () => {
            await assert.rejects(collectDiagnostics(root), (error: unknown) => {
                assert.ok(error instanceof AggregateError);
                assert.strictEqual(error.errors.length, 1);
                assert.strictEqual(error.errors[0].code, 'EBUSY');
                assert.strictEqual(error.errors[0].syscall, 'copyfile');
                assert.strictEqual(error.errors[0].path, lockPath);
                assert.ok(error.message.includes(lockPath), 'The aggregate must expose the source in CI output.');
                return true;
            });
            assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), {
                ...representativeDiagnostics,
                'aspire-home/logs/cli.log': 'http://localhost:1234/login?t=<redacted>\n',
                'settings/logs/session/window/Aspire Extension.log': 'Setting up RPC server with token: <redacted>\n',
            });
        });
    });

    test('reports failures from multiple sources without skipping later sources', async function () {
        if (process.platform !== 'win32') {
            this.skip();
        }
        const logLock = path.join(root, 'aspire-home', 'logs', 'unexpected.lock');
        const screenshotLock = path.join(root, 'storage', 'screenshots', 'unexpected.lock');

        await withExclusiveFileLock(logLock, async () => {
            await withExclusiveFileLock(screenshotLock, async () => {
                await assert.rejects(collectDiagnostics(root), (error: unknown) => {
                    assert.ok(error instanceof AggregateError);
                    assert.deepStrictEqual(error.errors.map(failure => ({
                        code: failure.code, syscall: failure.syscall, path: failure.path,
                    })), [
                        { code: 'EBUSY', syscall: 'copyfile', path: logLock },
                        { code: 'EBUSY', syscall: 'copyfile', path: screenshotLock },
                    ]);
                    return true;
                });
                assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), representativeDiagnostics);
            });
        });
    });

    test('reports a diagnostic disappearing after enumeration and collects other sources', async () => {
        const logPath = path.join(root, 'aspire-home', 'logs', 'cli.log');

        await assert.rejects(collectDiagnostics(root, {
            beforeCopy(sourcePath) {
                if (path.relative(logPath, sourcePath) === '') {
                    fs.rmSync(logPath);
                }
            },
        }), (error: unknown) => {
            assert.ok(error instanceof AggregateError);
            assert.strictEqual(error.errors.length, 1);
            assert.strictEqual(error.errors[0].code, 'ENOENT');
            assert.strictEqual(error.errors[0].path, logPath);
            return true;
        });
        const expected: Record<string, string> = { ...representativeDiagnostics };
        delete expected['aspire-home/logs/cli.log'];
        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), expected);
    });

    test('reports redaction read failures without writing unredacted text or skipping other diagnostics', async () => {
        writeFiles(root, {
            'aspire-home/logs/cli.log': 'http://localhost:1234/login?t=private-dashboard-token\n',
            'storage/settings/logs/session/window/Aspire Extension.log': 'http://localhost:1234/login?t=another-private-token\n',
        });

        await assert.rejects(collectDiagnostics(root, {
            beforeRead(filePath) {
                if (path.basename(filePath) === 'cli.log') {
                    // A real read failure after stat/copy, without OS permission or timing dependencies.
                    fs.rmSync(filePath);
                }
            },
        }), (error: unknown) => {
            assert.ok(error instanceof AggregateError);
            assert.strictEqual(error.errors.length, 1);
            assert.strictEqual(error.errors[0].code, 'ENOENT');
            return true;
        });
        const expected: Record<string, string> = {
            ...representativeDiagnostics,
            'settings/logs/session/window/Aspire Extension.log': 'http://localhost:1234/login?t=<redacted>\n',
        };
        delete expected['aspire-home/logs/cli.log'];
        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), expected);
    });

    test('preserves original log bytes when no redaction is needed', async () => {
        const contents = Buffer.from('\uFEFFCLI completed.\n', 'utf16le');
        fs.writeFileSync(path.join(root, 'aspire-home', 'logs', 'cli.log'), contents);

        await collectDiagnostics(root);

        assert.deepStrictEqual(fs.readFileSync(path.join(root, 'diagnostics', 'aspire-home', 'logs', 'cli.log')), contents);
    });

    test('does not follow diagnostic symlinks outside selected inputs', async () => {
        const linkPath = path.join(root, 'aspire-home', 'logs', 'runtime-state');
        const targetPath = path.join(root, 'runtime-state');
        writeFiles(targetPath, { 'private.json': '{"token":"private-token-outside-selected-inputs"}' });
        fs.symlinkSync(targetPath, linkPath, process.platform === 'win32' ? 'junction' : 'dir');

        await assert.rejects(collectDiagnostics(root), (error: unknown) => {
            assert.ok(error instanceof AggregateError);
            assert.strictEqual(error.errors.length, 1);
            assert.ok(error.message.includes(linkPath));
            return true;
        });
        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), representativeDiagnostics);
        assert.deepStrictEqual(readFiles(targetPath), { 'private.json': '{"token":"private-token-outside-selected-inputs"}' });
    });

    test('tolerates diagnostics sources that were never created', async () => {
        fs.rmSync(path.join(root, 'aspire-home'), { recursive: true });
        fs.rmSync(path.join(root, 'storage'), { recursive: true });

        await collectDiagnostics(root);
        await collectDiagnostics(root, {}, true);

        assert.strictEqual(fs.existsSync(path.join(root, 'diagnostics')), false);
        assert.strictEqual(fs.existsSync(path.join(root, 'workspace-diagnostics')), false);
    });

    test('collects and redacts workspace configuration and fixture sources without runtime state', async () => {
        writeFiles(path.join(root, 'workspace'), {
            ...representativeWorkspaceDiagnostics,
            '.aspire/integrations/package-restore/hash/restore.lock': '',
            '.aspire/integrations/apphosts/hash/project-layouts/prepare.lock': '',
            '.aspire/modules/generated.ts': 'Generated SDK cache.\n',
            '.aspire/other-runtime-state.json': 'Not selected diagnostics.\n',
            '.vscode/other-runtime-state.json': 'Not selected diagnostics.\n',
            'AspireE2E.AppHost/obj/project.assets.json': 'Build cache.\n',
            'UnrelatedProject/AppHost.cs': 'Not a generated E2E fixture.\n',
            'AspireE2E.WinUI/App.xaml': '<Application url="http://localhost:1234/login?t=private-token" />\n',
            'AspireE2E.AppHost/AspireE2E.AppHost.csproj': '<Project url="http://localhost:1234/login?t=private-token" />\n',
        });

        await collectDiagnostics(root, {}, true);

        assert.deepStrictEqual(readFiles(path.join(root, 'workspace-diagnostics')), {
            ...representativeWorkspaceDiagnostics,
            'AspireE2E.WinUI/App.xaml': '<Application url="http://localhost:1234/login?t=<redacted>" />\n',
            'AspireE2E.AppHost/AspireE2E.AppHost.csproj': '<Project url="http://localhost:1234/login?t=<redacted>" />\n',
        });
    });

    for (const [name, relativeLockPath] of [
        ['package restore', '.aspire/integrations/package-restore/hash/restore.lock'],
        ['project layout', '.aspire/integrations/apphosts/hash/project-layouts/prepare.lock'],
    ]) {
        test(`collects workspace diagnostics with an exclusively held ${name} lock`, async function () {
            if (process.platform !== 'win32') {
                this.skip();
            }
            const workspaceRoot = path.join(root, 'workspace');
            writeFiles(workspaceRoot, { ...representativeWorkspaceDiagnostics, [relativeLockPath]: '' });
            const lockPath = path.join(workspaceRoot, ...relativeLockPath.split('/'));

            await withExclusiveFileLock(lockPath, async () => {
                assert.throws(() => fs.copyFileSync(lockPath, path.join(root, 'control.lock')), { code: 'EBUSY' });
                const visitedRuntimePaths: string[] = [];
                await collectDiagnostics(root, {
                    beforeCopy(sourcePath) {
                        const relativePath = path.relative(workspaceRoot, sourcePath).split(path.sep).join('/');
                        if (relativePath === '.aspire/integrations' || relativePath.startsWith('.aspire/integrations/')) {
                            visitedRuntimePaths.push(relativePath);
                        }
                    },
                }, true);
                assert.deepStrictEqual(visitedRuntimePaths, []);
                assert.deepStrictEqual(readFiles(path.join(root, 'workspace-diagnostics')), representativeWorkspaceDiagnostics);
            });
            assert.strictEqual(fs.existsSync(lockPath), false);
        });
    }

    test('retains workspace settings and remaining fixture sources when a source fails', async () => {
        writeFiles(path.join(root, 'workspace'), representativeWorkspaceDiagnostics);
        const appHostPath = path.join(root, 'workspace', 'AspireE2E.AppHost', 'AppHost.cs');

        await assert.rejects(collectDiagnostics(root, {
            beforeRead(filePath) {
                if (path.basename(filePath) === 'AppHost.cs') {
                    fs.rmSync(filePath);
                }
            },
        }, true), (error: unknown) => {
            assert.ok(error instanceof AggregateError);
            assert.strictEqual(error.errors.length, 1);
            assert.strictEqual(error.errors[0].code, 'ENOENT');
            assert.strictEqual(error.errors[0].path, appHostPath);
            return true;
        });
        const expected: Record<string, string> = { ...representativeWorkspaceDiagnostics };
        delete expected['AspireE2E.AppHost/AppHost.cs'];
        assert.deepStrictEqual(readFiles(path.join(root, 'workspace-diagnostics')), expected);
    });
});
