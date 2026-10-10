import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { cp, mkdir, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const source = new URL('.', import.meta.url).pathname;
const root = resolve(source, '../..');
const work = await mkdtemp(join(root, 'artifacts/native-hosting/cli-work-'));
const resultsPath = process.env.NATIVE_HOSTING_CLI_RESULTS;
const expectFailure = process.env.NATIVE_HOSTING_EXPECT_CLI_FAILURE === '1';
// Native run allows 80s for integration disposal and in-tree DCP cleanup,
// followed by the CLI's final drain. Require process exit within that contract.
const shutdownTimeout = 90000;
await mkdir(join(work, '.aspire'));
await mkdir(join(work, 'fixture'));
await cp(join(source, expectFailure ? 'CliAppHost/failing-apphost.mts' : 'CliAppHost/apphost.mts'), join(work, 'apphost.mts'));
await cp(join(source, 'CliAppHost/aspire.config.json'), join(work, 'aspire.config.json'));
await symlink(resolve(source, '../NuxtApp/node_modules'), join(work, 'node_modules'), 'dir');
await cp(join(source, 'web'), join(work, 'web'), { recursive: true });
await symlink(resolve(source, '../NuxtApp/web/node_modules'), join(work, 'web/node_modules'), 'dir');
await cp(resolve(source, '../NuxtApp/web/package.json'), join(work, 'web/package.json'));
const executable = resolve(root, '.dotnet/dotnet');
const cli = resolve(process.env.NATIVE_HOSTING_CLI_DLL ?? join(root, 'artifacts/bin/Aspire.Cli/Debug/net11.0/aspire.dll'));
const child = spawn(executable, [cli, 'run', '--apphost', join(work, 'apphost.mts'), '--non-interactive', '--nologo', '--log-level', 'Debug'], {
    cwd: work, stdio: ['ignore', 'pipe', 'pipe'],
    env: {
        ...process.env, DOTNET_ROOT: resolve(root, '.dotnet'),
        ASPIRE_CLI_NATIVE_APPHOST_SERVER: resolve(process.env.NATIVE_HOSTING_ATS_BINARY ?? join(root, 'artifacts/native-hosting/ats-server/NativeHosting.AtsServer')),
        NATIVE_HOSTING_CLI: '1',
        NATIVE_HOSTING_INTEGRATION_HOST: join(source, 'ats-integration-host.mts'),
        NATIVE_HOSTING_DCP: process.env.NATIVE_HOSTING_DCP ?? '/Users/davidfowler/.nuget/packages/microsoft.developercontrolplane.darwin-arm64/0.26.5/tools/dcp',
        NATIVE_HOSTING_GUEST_DIRECTORY: work, NATIVE_HOSTING_PORT_DATA_DIRECTORY: work,
        NATIVE_HOSTING_TUNNEL_FIXTURE: join(work, 'fixture'),
        ASPIRE_CLI_TELEMETRY_OPTOUT: 'true',
    },
});
let output = '';
child.stdout.on('data', chunk => { output += chunk.toString(); process.stdout.write(chunk); });
child.stderr.on('data', chunk => { output += chunk.toString(); process.stderr.write(chunk); });
const exited = new Promise<{ code: number | null; signal: NodeJS.Signals | null }>((fulfill, reject) => {
    child.once('error', reject);
    child.once('close', (code, signal) => fulfill({ code, signal }));
});
async function waitFor(predicate: () => Promise<boolean>, description: string, timeout = 240000) {
    const deadline = Date.now() + timeout;
    while (!await predicate()) {
        if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${description}.`);
        await delay(100);
    }
}
try {
    if (expectFailure) {
        const exit = await Promise.race([exited, delay(shutdownTimeout, undefined, { ref: false }).then(() => { throw new Error('Failed guest left aspire run hanging.'); })]);
        assert.ok(exit.code !== null && exit.code !== 0 && exit.code !== 130, `Guest failure must produce a nonzero exit: ${JSON.stringify(exit)}`);
        assert.match(output, /Intentional native CLI guest failure/);
        console.log(JSON.stringify({ realAspireRun: true, guestFailurePropagated: true, exit }));
    } else {
        let result: { containerIds: string[]; webUrl: string; stats: { dynamicCodeSupported: boolean } } | undefined;
        await waitFor(async () => {
            if (child.exitCode !== null || child.signalCode !== null) throw new Error(`aspire run exited before workload readiness.\n${output}`);
            try { result = JSON.parse(await readFile(join(work, 'cli-result.json'), 'utf8')); return true; }
            catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false; throw error; }
        }, 'actual aspire run workload checks');
        // This marker is emitted only after RunCommand consumes the native adapter,
        // observes graph readiness, and sends its normal AppHost-ready notification.
        await waitFor(async () => output.includes('Native graph reported ready to Aspire CLI.'), 'CLI control-plane readiness');
        assert.ok(result);
        assert.equal(result.stats.dynamicCodeSupported, false);
        assert.equal((await fetch(new URL('/api/health', result.webUrl))).status, 200);
        child.kill('SIGINT');
        const exit = await Promise.race([exited, delay(shutdownTimeout, undefined, { ref: false }).then(() => { throw new Error('CLI did not stop within its shutdown budget.'); })]);
        assert.ok(exit.code === 0 || exit.code === 130, `Unexpected CLI exit: ${JSON.stringify(exit)}`);
        for (const id of result.containerIds) {
            await waitFor(async () => execFileSync('docker', ['ps', '-aq', '--filter', `id=${id}`], { encoding: 'utf8' }).trim() === '',
                `owned container cleanup: ${id}`, 15000);
        }
        const evidence = { ...result, cliReadinessObserved: true, cleanShutdown: true, exit };
        if (resultsPath) await writeFile(resultsPath, JSON.stringify(evidence, null, 2));
        console.log(JSON.stringify(evidence, null, 2));
    }
} finally {
    if (child.exitCode === null && child.signalCode === null) {
        child.kill('SIGINT');
        await Promise.race([exited, delay(shutdownTimeout, undefined, { ref: false }).then(() => { child.kill('SIGKILL'); })]);
    }
    await rm(work, { recursive: true, force: true });
}
