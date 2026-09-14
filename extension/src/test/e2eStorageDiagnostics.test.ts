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
    'aspire-home/config.json': '{"channel":"daily"}\n',
    'settings/logs/session/window/Aspire Extension.log': 'Extension stopped.\n',
    'settings/User/settings.json': '{"aspire.viewMode":"workspace"}\n',
    'settings/CrashpadMetrics-active.pma': 'crash metrics',
    'screenshots/failure.png': 'screenshot bytes',
};

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

function collectDiagnostics(root: string, onFilter?: (sourcePath: string) => void): void {
    const exports: { copyStorageDiagnostics?: () => void } = {};
    // Execute the real collector and its helpers without running module setup or main(), which
    // allocate an E2E workspace and launch VS Code. Other runner contract tests use the same AST/VM pattern.
    vm.runInNewContext(`${runnerFunctions}\nexports.copyStorageDiagnostics = copyStorageDiagnostics;`, {
        exports,
        path,
        process,
        isWindows: process.platform === 'win32',
        isolatedAspireHome: path.join(root, 'aspire-home'),
        storageDir: path.join(root, 'storage'),
        storageDiagnosticsDir: path.join(root, 'diagnostics'),
        fs: {
            ...fs,
            cpSync(sourcePath: string | URL, destinationPath: string | URL, options?: fs.CopySyncOptions) {
                const filter = options?.filter;
                // Keep real filesystem operations, including failures. This hook changes source files
                // after enumeration but before the runner's filter, without racing another test thread.
                fs.cpSync(sourcePath, destinationPath, filter && onFilter ? {
                    ...options,
                    filter(source, destination) {
                        onFilter(source);
                        return filter(source, destination);
                    },
                } : options);
            },
        },
    });
    assert.ok(exports.copyStorageDiagnostics);
    exports.copyStorageDiagnostics();
}

async function withExclusiveFileLock(filePath: string, action: () => void): Promise<void> {
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
        action();
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

    test('retains and redacts diagnostics while excluding Dashboard persistence and CLI leases', () => {
        const retainedFiles = {
            'aspire-home/logs/dashboard/session.log': 'Dashboard diagnostics outside persistence.\n',
            'aspire-home/cache/apphost-info/state.lock': 'An unrelated lock-named diagnostic.\n',
            'aspire-home/dashboard-backup/info.json': '{"retained":true}\n',
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
            'aspire-home/cache/apphost-info/.leases/app.json': 'lease state',
            'aspire-home/cache/apphost-info/app.lease': '',
            'aspire-home/legacy.lease': '',
        });

        collectDiagnostics(root);

        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), {
            ...representativeDiagnostics,
            ...retainedFiles,
            'aspire-home/logs/cli.log': 'http://localhost:1234/login?t=<redacted>\n',
        });
        assert.strictEqual(fs.existsSync(path.join(root, 'diagnostics', 'aspire-home', 'dashboard')), false);
    });

    test('does not descend into Dashboard persistence when run files disappear', () => {
        writeFiles(root, {
            'aspire-home/dashboard/runs/20260914T045039914Z.lock': '',
            'aspire-home/dashboard/runs/20260914T045039914Z/run.json': '{}',
        });
        const visitedDashboardPaths: string[] = [];

        collectDiagnostics(root, sourcePath => {
            const relativePath = path.relative(path.join(root, 'aspire-home'), sourcePath).split(path.sep).join('/');
            if (relativePath === 'dashboard') {
                // Run locks are DeleteOnClose, and retention can also remove entire run directories.
                fs.rmSync(path.join(sourcePath, 'runs'), { recursive: true });
            }
            if (relativePath === 'dashboard' || relativePath.startsWith('dashboard/')) {
                visitedDashboardPaths.push(relativePath);
            }
        });

        assert.deepStrictEqual(visitedDashboardPaths, ['dashboard']);
        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), representativeDiagnostics);
        assert.strictEqual(fs.existsSync(path.join(root, 'diagnostics', 'aspire-home', 'dashboard')), false);
    });

    test('uses case-insensitive exclusions on Windows', function () {
        if (process.platform !== 'win32') {
            this.skip();
        }
        writeFiles(root, {
            'aspire-home/DASHBOARD/runs/app.lock': '',
            'aspire-home/cache/apphost-info/.LEASES/app.json': 'lease state',
            'aspire-home/cache/apphost-info/app.LEASE': '',
        });

        collectDiagnostics(root);

        assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), representativeDiagnostics);
    });

    for (const persistenceMode of ['runs', 'resumes']) {
        test(`collects VS Code and Aspire diagnostics with an exclusively held Dashboard ${persistenceMode} lock`, async function () {
            if (process.platform !== 'win32') {
                this.skip();
            }
            const relativeLockPath = `aspire-home/dashboard/${persistenceMode}/20260914T045039914Z.lock`;
            writeFiles(root, { [relativeLockPath]: '' });
            const lockPath = path.join(root, ...relativeLockPath.split('/'));

            await withExclusiveFileLock(lockPath, () => {
                // A control copy proves the OS lock is actually held, rather than just testing its name.
                assert.throws(() => fs.copyFileSync(lockPath, path.join(root, 'control.lock')), { code: 'EBUSY' });

                collectDiagnostics(root);

                assert.strictEqual(fs.existsSync(lockPath), true);
                assert.deepStrictEqual(readFiles(path.join(root, 'diagnostics')), representativeDiagnostics);
            });
            assert.strictEqual(fs.existsSync(lockPath), false, 'The lock must be deleted when its owner exits.');
        });
    }

    test('does not suppress an unexpected locked diagnostic', async function () {
        if (process.platform !== 'win32') {
            this.skip();
        }
        const lockPath = path.join(root, 'aspire-home', 'logs', 'unexpected.lock');

        await withExclusiveFileLock(lockPath, () => {
            assert.throws(() => collectDiagnostics(root), { code: 'EBUSY', syscall: 'copyfile', path: lockPath });
        });
    });

    test('does not suppress an unexpected diagnostic disappearing during copy', () => {
        const logPath = path.join(root, 'aspire-home', 'logs', 'cli.log');

        assert.throws(() => collectDiagnostics(root, sourcePath => {
            if (path.relative(logPath, sourcePath) === '') {
                fs.rmSync(logPath);
            }
        }), { code: 'ENOENT', path: logPath });
    });

    test('tolerates diagnostics sources that were never created', () => {
        fs.rmSync(path.join(root, 'aspire-home'), { recursive: true });
        fs.rmSync(path.join(root, 'storage'), { recursive: true });

        collectDiagnostics(root);

        assert.strictEqual(fs.existsSync(path.join(root, 'diagnostics')), false);
    });
});
