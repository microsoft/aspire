import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createGoRunProcessIdentity } from '../debugger/languages/go';
import {
    LaunchedChildProcessResolver,
    parsePosixProcessList,
    parseWindowsProcessList,
    SystemLaunchedChildProcessQuery,
    type LaunchedChildProcess as GoProcessInfo,
    type LaunchedChildProcessClock as GoProcessDiscoveryClock,
    type LaunchedChildProcessCommandRunner as GoProcessCommandRunner,
    type LaunchedChildProcessQuery as GoProcessQuery,
} from '../debugger/launchedChildProcessDiscovery';

class TestClock implements GoProcessDiscoveryClock {
    private _now = 0;

    now(): number {
        return this._now;
    }

    async sleep(milliseconds: number, cancellationToken?: vscode.CancellationToken): Promise<void> {
        if (cancellationToken?.isCancellationRequested) {
            throw new vscode.CancellationError();
        }

        this._now += milliseconds;
    }
}

class SequenceProcessQuery implements GoProcessQuery {
    private _index = 0;

    constructor(private readonly _snapshots: readonly (readonly GoProcessInfo[] | Error)[]) {
    }

    async listProcesses(): Promise<readonly GoProcessInfo[]> {
        const snapshot = this._snapshots[Math.min(this._index, this._snapshots.length - 1)];
        this._index++;
        if (snapshot instanceof Error) {
            throw snapshot;
        }

        return snapshot;
    }
}

function process(pid: number, parentPid: number, executable: string, command = executable): GoProcessInfo {
    return { pid, parentPid, executable, command };
}

function goRunProcessTree(applicationPid = 42): readonly GoProcessInfo[] {
    return [
        process(10, 1, '/usr/local/go/bin/go', 'go run ./cmd/api'),
        process(22, 10, '/usr/local/go/pkg/tool/darwin_arm64/compile'),
        process(33, 22, '/usr/local/go/pkg/tool/darwin_arm64/link'),
        process(applicationPid, 33, `/private/var/folders/x/go-build123/b001/exe/api`, `/private/var/folders/x/go-build123/b001/exe/api --port 8080`),
    ];
}

function createGoRunApplicationProcessResolver(
    processQuery: GoProcessQuery,
    clock?: GoProcessDiscoveryClock,
    options?: { readonly timeoutMs?: number; readonly retryDelayMs?: number },
): { resolveApplicationPid(goProcessId: number, cancellationToken?: vscode.CancellationToken): Promise<number> } {
    const resolver = new LaunchedChildProcessResolver(processQuery, clock, options);
    const identity = createGoRunProcessIdentity();
    return {
        resolveApplicationPid: async (goProcessId, cancellationToken) =>
            await resolver.resolveProcessId(goProcessId, identity, cancellationToken),
    };
}

suite('Go process discovery', () => {
    teardown(() => sinon.restore());

    test('parses POSIX process topology without retaining incomplete rows', () => {
        assert.deepStrictEqual(parsePosixProcessList([
            '  10     1',
            '  42    10',
            'not a process row',
        ].join('\n')), [
            process(10, 1, '', ''),
            process(42, 10, '', ''),
        ]);
    });

    test('parses Windows CIM process listings', () => {
        assert.deepStrictEqual(parseWindowsProcessList(JSON.stringify([
            {
                ProcessId: 10,
                ParentProcessId: 1,
                Name: 'go.exe',
                ExecutablePath: 'C:\\Go\\bin\\go.exe',
                CommandLine: 'go run .\\cmd\\api',
            },
            {
                ProcessId: 42,
                ParentProcessId: 10,
                Name: 'api.exe',
                ExecutablePath: 'C:\\Users\\me\\AppData\\Local\\Temp\\go-build123\\b001\\exe\\api.exe',
                CommandLine: 'C:\\Users\\me\\AppData\\Local\\Temp\\go-build123\\b001\\exe\\api.exe',
            },
        ])), [
            process(10, 1, 'C:\\Go\\bin\\go.exe', 'go run .\\cmd\\api'),
            process(42, 10, 'C:\\Users\\me\\AppData\\Local\\Temp\\go-build123\\b001\\exe\\api.exe', 'C:\\Users\\me\\AppData\\Local\\Temp\\go-build123\\b001\\exe\\api.exe'),
        ]);
    });

    test('uses fixed platform-specific process discovery commands', async () => {
        const calls: Array<{ command: string; args: readonly string[] }> = [];
        const commandRunner: GoProcessCommandRunner = {
            async run(command, args): Promise<string> {
                calls.push({ command, args });
                return command === 'ps'
                    ? '10 1 /usr/local/go/bin/go go run ./cmd/api'
                    : JSON.stringify({
                        ProcessId: 10,
                        ParentProcessId: 1,
                        Name: 'go.exe',
                        ExecutablePath: 'C:\\Go\\bin\\go.exe',
                        CommandLine: 'go run .\\cmd\\api',
                    });
            },
        };

        await new SystemLaunchedChildProcessQuery('linux', commandRunner).listProcesses();
        await new SystemLaunchedChildProcessQuery('win32', commandRunner).listProcesses();

        assert.deepStrictEqual(calls, [
            {
                command: 'ps',
                args: ['-axo', 'pid=,ppid='],
            },
            {
                command: 'powershell.exe',
                args: [
                    '-NoLogo',
                    '-NoProfile',
                    '-NonInteractive',
                    '-Command',
                    '$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine | ConvertTo-Json -Compress',
                ],
            },
        ]);
    });

    test('traverses nested children and ignores Go toolchain processes', async () => {
        const resolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([goRunProcessTree(), goRunProcessTree()]),
            new TestClock(),
            { timeoutMs: 100, retryDelayMs: 10 });

        assert.strictEqual(await resolver.resolveApplicationPid(10), 42);
    });

    test('resolves a cached Go run application and ignores linker output paths', async () => {
        const cachedApplication = process(
            42,
            10,
            '/Users/me/Library/Caches/go-build/8a/8a26e5d38d9d4f6e7c8b0a1d2e3f4c5b6a7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f-d/api',
            '/Users/me/Library/Caches/go-build/8a/8a26e5d38d9d4f6e7c8b0a1d2e3f4c5b6a7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f-d/api --port 8080');
        const linker = process(
            33,
            10,
            '/usr/local/go/pkg/tool/darwin_arm64/link',
            '/usr/local/go/pkg/tool/darwin_arm64/link -o /private/var/folders/x/go-build123/b001/exe/api');
        const processTree = [
            process(10, 1, '/usr/local/go/bin/go', 'go run ./cmd/api'),
            linker,
            cachedApplication,
        ];
        const resolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([processTree, processTree]),
            new TestClock(),
            { timeoutMs: 100, retryDelayMs: 10 });

        assert.strictEqual(await resolver.resolveApplicationPid(10), 42);
    });

    test('does not trust a Go launcher command when executable identity differs', () => {
        const identity = createGoRunProcessIdentity();

        assert.strictEqual(identity.isLauncher(process(
            10,
            1,
            '/Users/me/Very',
            '/Users/me/Very Long Go Installation/bin/go run ./cmd/api')), false);
        assert.strictEqual(identity.isLauncher(process(
            11,
            1,
            '/bin/bash',
            'bash -c "go run ./cmd/api"')), false);
        assert.strictEqual(identity.isCandidate(process(
            42,
            10,
            '/usr/bin/other',
            '/private/var/folders/x/go-build123/b001/exe/api')), false);
    });

    test('waits for the same Go build application candidate twice', async () => {
        const resolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([
                goRunProcessTree(42),
                goRunProcessTree(43),
                goRunProcessTree(43),
            ]),
            new TestClock(),
            { timeoutMs: 100, retryDelayMs: 10 });

        assert.strictEqual(await resolver.resolveApplicationPid(10), 43);
    });

    test('fails closed when no Go build application process exists', async () => {
        const resolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([
                [process(10, 1, '/usr/local/go/bin/go', 'go run ./cmd/api')],
            ]),
            new TestClock(),
            { timeoutMs: 20, retryDelayMs: 10 });

        await assert.rejects(resolver.resolveApplicationPid(10));
    });

    test('fails closed when Go build application candidates are ambiguous', async () => {
        const resolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([
                [
                    ...goRunProcessTree(42),
                    process(43, 10, '/private/var/folders/x/go-build456/b001/exe/worker'),
                ],
            ]),
            new TestClock(),
            { timeoutMs: 20, retryDelayMs: 10 });

        await assert.rejects(resolver.resolveApplicationPid(10));
    });

    test('fails closed when the reported Go parent process was reused', async () => {
        const resolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([
                [
                    process(10, 1, '/bin/bash', 'bash build.sh'),
                    process(42, 10, '/private/var/folders/x/go-build123/b001/exe/api'),
                ],
            ]),
            new TestClock(),
            { timeoutMs: 20, retryDelayMs: 10 });

        await assert.rejects(resolver.resolveApplicationPid(10));
    });

    test('fails within its bounded timeout without a stable candidate', async () => {
        const resolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([
                goRunProcessTree(42),
                goRunProcessTree(43),
                goRunProcessTree(42),
            ]),
            new TestClock(),
            { timeoutMs: 20, retryDelayMs: 10 });

        await assert.rejects(resolver.resolveApplicationPid(10));
    });

    test('propagates cancellation and process-query failures without process details', async () => {
        const cancellation = new vscode.CancellationTokenSource();
        const failedResolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([new Error('/private/go-build123/b001/exe/api 4242')]),
            new TestClock(),
            { timeoutMs: 20, retryDelayMs: 10 });
        const cancelledResolver = createGoRunApplicationProcessResolver(
            new SequenceProcessQuery([goRunProcessTree()]),
            new TestClock(),
            { timeoutMs: 20, retryDelayMs: 10 });

        try {
            await assert.rejects(
                failedResolver.resolveApplicationPid(10),
                error => error instanceof Error && !/go-build|4242/.test(error.message));
            cancellation.cancel();
            await assert.rejects(cancelledResolver.resolveApplicationPid(10, cancellation.token), vscode.CancellationError);
        }
        finally {
            cancellation.dispose();
        }
    });
});
