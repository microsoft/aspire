// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';

const execute = promisify(execFile);
const [cliPath, serverPath, repository, workRoot] = process.argv.slice(2);
assert.ok(cliPath && serverPath && repository && workRoot && process.env.ASPIRE_LAYOUT_PATH);
await mkdir(workRoot, { recursive: true });
const workspace = await mkdtemp(join(workRoot, 'compose-'));
const sample = resolve(repository, 'playground/NativeIntegrations/TypeScript/DockerCompose');
await cp(join(sample, 'AppHost'), workspace, { recursive: true });
await mkdir(join(workspace, 'provider'));
for (const file of ['docker-compose.mts', 'aspire.apphost-server.json', 'provider-contract.json']) {
    await cp(join(sample, file), join(workspace, 'provider', file));
}
await cp(resolve(repository, 'tests/PolyglotAppHosts/Aspire.Hosting/TypeScript/package.json'), join(workspace, 'package.json'));
await cp(resolve(repository, 'src/Aspire.Cli/Templating/Templates/ts-starter/tsconfig.apphost.json'), join(workspace, 'tsconfig.apphost.json'));
await writeFile(join(workspace, 'provider/package.json'), JSON.stringify({ name: 'example.ts.docker-compose', version: '1.0.0', type: 'module' }));
await writeFile(join(workspace, 'aspire.config.json'), JSON.stringify({
    appHost: { path: 'apphost.mts', language: 'typescript/nodejs' },
    features: { appHostServerV2: true },
    packages: { 'example.ts.docker-compose': { source: 'npm', path: './provider/docker-compose.mts' } }
}));
const image = `aspire-native-compose-${randomBytes(6).toString('hex')}:test`;
const password = `Compose-$@:/?#% ${randomBytes(12).toString('hex')}`;
const environment: NodeJS.ProcessEnv = { ...process.env, ASPIRE_CLI_NATIVE_APPHOST_SERVER: resolve(serverPath), ASPIRE_COMPOSE_SAMPLE_IMAGE: image };
delete environment.ASPIRE_PARAMETER_CACHE_PASSWORD;
const output = join(workspace, 'published');
let project: string | undefined;
let ownsProject = false;
let commandNumber = 0;
function safe(text: string): string {
    return text.replaceAll(password, '[redacted]').replaceAll(encodeURIComponent(password), '[redacted]');
}
async function run(command: string, arguments_: string[], env: NodeJS.ProcessEnv = environment, expected: number | 'failure' = 0): Promise<{ stdout: string; diagnostics: string }> {
    let result;
    try {
        result = { ...await execute(command, arguments_, { cwd: workspace, env, timeout: 240_000, maxBuffer: 4 * 1024 * 1024 }), code: 0 };
    } catch (error) {
        if (!(error instanceof Error && 'stdout' in error && 'stderr' in error && 'code' in error)) throw error;
        result = { stdout: String(error.stdout), stderr: String(error.stderr), code: error.code };
    }
    const stdout = safe(result.stdout), stderr = safe(result.stderr);
    await writeFile(join(workspace, `${++commandNumber}-${command === cliPath ? 'aspire' : 'docker'}.log`), stdout + stderr);
    const diagnostics = `${command} ${arguments_.join(' ')}\n${stdout}\n${stderr}`;
    if (expected === 'failure') {
        assert.equal(typeof result.code, 'number', diagnostics);
        assert.notEqual(result.code, 0, diagnostics);
    } else {
        assert.equal(result.code, expected, diagnostics);
    }
    return { stdout, diagnostics: stdout + stderr };
}
async function cli(command: 'publish' | 'deploy' | 'do', extra: string[] = [], env = environment, expected: number | 'failure' = 0,
    outputPath = output) {
    return run(cliPath, [command, '--apphost', 'apphost.mts', '--output-path', outputPath, '--non-interactive', '--nologo', ...extra], env, expected);
}
async function owned(kind: 'container' | 'volume' | 'network'): Promise<string[]> {
    assert.ok(project);
    const { stdout: text } = await run('docker', [kind, 'ls', ...(kind === 'container' ? ['--all'] : []),
        '--filter', `label=com.docker.compose.project=${project}`, '--format', kind === 'volume' ? '{{.Name}}' : '{{.ID}}']);
    return text.trim().split(/\r?\n/).filter(Boolean);
}
async function visits(url: string): Promise<number> {
    const deadline = Date.now() + 60_000;
    for (;;) {
        const response = await fetch(url, { signal: AbortSignal.timeout(3000) });
        if (response.ok) {
            const data = await response.json() as { value: string };
            assert.match(data.value, /^\d+$/);
            return Number(data.value);
        }
        if (Date.now() >= deadline) throw new Error('Deployed service did not become ready.');
        await delay(250);
    }
}
async function deployedUrl(): Promise<string> {
    const state = JSON.parse(await readFile(join(output, 'deployment-state.json'), 'utf8')) as {
        services: { endpoints: { url?: string }[] }[];
    };
    const url = state.services.flatMap(service => service.endpoints).find(endpoint => endpoint.url?.startsWith('http://'))?.url;
    assert.ok(url);
    assert.equal(new URL(url).hostname, '127.0.0.1');
    return url;
}
async function localLifecycle(env: NodeJS.ProcessEnv): Promise<void> {
    const volume = `aspire-native-lifecycle-${randomBytes(8).toString('hex')}`;
    const apphost = join(workspace, 'apphost.mts');
    await writeFile(apphost, (await readFile(apphost, 'utf8'))
        .replace('builder.getDefaultDataVolumeName(cache.resource)', JSON.stringify(volume)));
    const localEnvironment = { ...env, ASPIRE_HOME: join(workspace, 'local-home') };
    const arguments_ = ['--apphost', 'apphost.mts', '--non-interactive', '--nologo'];
    const child = spawn(cliPath!, ['run', ...arguments_], { cwd: workspace, env: localEnvironment });
    child.stdin.end();
    let foreground = '';
    let launchError: Error | undefined;
    child.stdout.on('data', data => { foreground += data; });
    child.stderr.on('data', data => { foreground += data; });
    child.once('error', error => { launchError = error; });
    const exited = new Promise<number | null>(accept => child.once('close', code => accept(code)));
    let active = true;
    async function ready(): Promise<number> {
        const deadline = Date.now() + 120_000;
        let lastError: unknown;
        while (Date.now() < deadline) {
            if (launchError) throw launchError;
            if (child.exitCode !== null && !detached) throw new Error(`Foreground CLI exited ${child.exitCode}: ${safe(foreground)}`);
            try {
                const result = await run(cliPath!, ['describe', ...arguments_, '--format', 'Json'], localEnvironment);
                const snapshot = JSON.parse(result.stdout) as {
                    resources: { name: string; state: string; urls: { url: string }[] }[];
                };
                assert.equal(snapshot.resources.find(resource => resource.name === 'cache')?.state, 'Running');
                const web = snapshot.resources.find(resource => resource.name === 'web');
                assert.equal(web?.state, 'Running');
                const url = web?.urls.find(endpoint => endpoint.url.startsWith('http://'))?.url;
                assert.ok(url);
                return await visits(url);
            } catch (error) {
                lastError = error;
                await delay(250);
            }
        }
        throw new Error('Local sample did not become responsive.', { cause: lastError });
    }
    async function stop(pid: number | undefined): Promise<void> {
        await run(cliPath!, ['stop', ...arguments_, ...(pid === undefined ? [] : ['--pid', String(pid)])], localEnvironment);
        active = false;
    }
    let detached = false;
    try {
        const first = await ready();
        const listed = await run(cliPath!, ['ps', '--format', 'Json', '--non-interactive', '--nologo'], localEnvironment);
        const hosts = JSON.parse(listed.stdout) as { appHostPath: string; appHostPid: number; cliPid: number }[];
        const ownedHosts = hosts.filter(host => resolve(host.appHostPath) === apphost && host.cliPid === child.pid);
        assert.equal(ownedHosts.length, 1, 'Find only this fixture and its foreground CLI owner.');
        const pid = ownedHosts[0]!.appHostPid;
        assert.ok(Number.isSafeInteger(pid) && pid > 0);
        // Instance-targeted RPC stop lets the foreground CLI exit normally.
        // Project-wide stop intentionally force-cleans remaining launcher handles.
        await stop(pid);
        const code = await Promise.race([exited, delay(30_000, undefined, { ref: false }).then(() => {
            throw new Error('Foreground CLI did not exit after stop.');
        })]);
        assert.equal(code, 0, safe(foreground));
        await run(cliPath!, ['start', ...arguments_, '--format', 'Json'], localEnvironment);
        active = true;
        detached = true;
        const retained = await ready();
        assert.ok(retained > first, 'Detached start must reuse authenticated Redis data.');
        await stop(undefined);
        await writeFile(join(workspace, 'local-proof.json'), JSON.stringify({ run: true, start: true, stop: true, first, retained }));
    } finally {
        try {
            if (active) await stop(undefined);
        } finally {
            if (!launchError && child.exitCode === null) child.kill('SIGTERM');
            await writeFile(join(workspace, 'foreground-run.log'), safe(foreground));
            const listed = await run('docker', ['volume', 'ls', '--filter', `name=${volume}`, '--format', '{{.Name}}']);
            if (listed.stdout.trim().split(/\r?\n/).includes(volume)) await run('docker', ['volume', 'rm', volume]);
        }
    }
}
try {
    const { stdout: listed } = await cli('do', ['verify-publish', '--list-steps', '--format', 'json']);
    const steps = JSON.parse(listed.trim()) as { name: string; dependsOn: string[]; tags: string[] }[];
    const names = steps.map(step => step.name);
    for (const expected of ['publish', 'publish-compose', 'policy-left', 'policy-right', 'cache-policy', 'verify-publish']) {
        assert.ok(names.includes(expected), `Missing pipeline step '${expected}': ${listed}`);
    }
    assert.deepEqual(steps.find(step => step.name === 'verify-publish')!.dependsOn, ['publish']);
    assert.ok(steps.find(step => step.name === 'cache-policy')!.tags.includes('validated'));
    await assert.rejects(readFile(join(output, 'compose.yaml')), { code: 'ENOENT' });
    await assert.rejects(readFile(join(output, 'pipeline/left.started')), { code: 'ENOENT' });
    await cp(resolve(repository, 'tests/PolyglotAppHosts/Aspire.Hosting.Docker/TypeScript/apphost.mts'), join(workspace, 'compatibility.mts'));
    await run(join(workspace, 'node_modules/.bin/tsc'), ['--module', 'NodeNext', '--moduleResolution', 'NodeNext', '--target', 'ES2022',
        '--strict', '--skipLibCheck', '--noEmit', 'compatibility.mts']);
    const { stdout: filtered } = await cli('deploy', ['--list-steps', '--format', 'json']);
    const filteredNames = (JSON.parse(filtered.trim()) as { name: string }[]).map(step => step.name);
    assert.ok(filteredNames.includes('deploy-compose'));
    assert.ok(filteredNames.includes('deploy'));
    assert.equal(filteredNames.includes('verify-publish'), false);
    const { diagnostics: published } = await cli('publish', ['--log-level', 'Debug']);
    await cli('do', ['verify-publish']);
    await readFile(join(output, 'pipeline/custom-target.complete'));
    const compose = JSON.parse(await readFile(join(output, 'compose.yaml'), 'utf8')) as { name: string; services: Record<string, unknown> };
    project = compose.name;
    assert.match(project, /^[a-z0-9][a-z0-9_-]+$/);
    assert.deepEqual(Object.keys(compose.services).sort(), ['cache', 'web']);
    assert.deepEqual(await owned('container'), []);
    assert.deepEqual(await owned('volume'), []);
    assert.deepEqual(await owned('network'), []);
    assert.equal((await run('docker', ['image', 'ls', image, '--format', '{{.ID}}'])).stdout.trim(), '');
    ownsProject = true;
    const serverPids = [...published.matchAll(/Aspire\.Hosting\.Native\.Server\((\d+)\) started/g)].map(match => Number(match[1]));
    assert.ok(serverPids.length > 0, 'Publishing must use the real product native server.');
    for (const pid of serverPids) {
        assert.throws(() => process.kill(pid, 0), (error: unknown) =>
            error instanceof Error && 'code' in error && error.code === 'ESRCH');
    }
    const missing = await cli('deploy', ['--log-level', 'Debug'], environment, 'failure');
    assert.match(missing.diagnostics, /ASPIRE_PARAMETER_CACHE_PASSWORD/);
    assert.deepEqual(await owned('container'), []);
    const deployEnvironment = { ...environment, ASPIRE_PARAMETER_CACHE_PASSWORD: password };
    await cli('deploy', [], deployEnvironment);
    const first = await visits(await deployedUrl());
    assert.equal(first, 1);
    const containers = await owned('container');
    assert.equal(containers.length, 2);
    const volumes = await owned('volume');
    assert.equal(volumes.length, 1);
    // Remove only this deployment's containers, not its retained volume. A fresh
    // AppHost/server and Compose application must recover the Redis data.
    await run('docker', ['container', 'rm', '--force', ...containers]);
    assert.deepEqual(await owned('container'), []);
    await cli('deploy', [], deployEnvironment);
    const retained = await visits(await deployedUrl());
    assert.equal(retained, 2);
    await cli('deploy', [], deployEnvironment);
    assert.equal(await visits(await deployedUrl()), 3);
    assert.deepEqual(await owned('volume'), volumes);
    for (const file of ['compose.yaml', 'deployment-inputs.json', 'deployment-state.json', 'prerequisites.txt']) {
        const content = await readFile(join(output, file), 'utf8');
        assert.equal(content.includes(password), false, file);
        assert.equal(content.includes(encodeURIComponent(password)), false, file);
    }
    await localLifecycle(deployEnvironment);
    await writeFile(join(workspace, 'apphost.mts'), `
        import { createBuilder } from './.aspire/modules/aspire.mjs';
        const builder = await createBuilder();
        for (const name of ['first', 'second']) {
            const environment = await builder.addDockerComposeEnvironment(name);
            await environment.withDashboard({ enabled: false });
            await builder.addContainer(name + '-api', 'nginx:alpine').withComputeEnvironment(environment);
        }
        await builder.build().run();
    `);
    const multipleOutput = join(workspace, 'multiple');
    await cli('publish', [], environment, 0, multipleOutput);
    const files = (await readdir(multipleOutput, { recursive: true })).filter(file => file.endsWith('compose.yaml'));
    assert.equal(files.length, 2);
    const services: string[][] = [];
    for (const file of files) {
        const document = JSON.parse(await readFile(join(multipleOutput, file), 'utf8')) as { services: Record<string, unknown> };
        services.push(Object.keys(document.services));
    }
    assert.deepEqual(services.sort((first, second) => first[0]!.localeCompare(second[0]!)), [['first-api'], ['second-api']]);
    await writeFile(join(workspace, 'apphost.mts'), `
        import { createBuilder } from './.aspire/modules/aspire.mjs';
        const builder = await createBuilder();
        const first = await builder.addDockerComposeEnvironment('first');
        const second = await builder.addDockerComposeEnvironment('second');
        await first.withDashboard({ enabled: false });
        await second.withDashboard({ enabled: false });
        const one = builder.addContainer('one', 'nginx:alpine').withComputeEnvironment(first);
        const two = builder.addContainer('two', 'nginx:alpine').withComputeEnvironment(second).withHttpEndpoint({ targetPort: 80 });
        one.withEnvironment('OTHER', two.getEndpoint('http').hostAndPort);
        await builder.build().run();
    `);
    const cross = await cli('publish', ['--log-level', 'Debug'], environment, 'failure', join(workspace, 'cross'));
    assert.match(cross.diagnostics, /environment/i);

    await writeFile(join(workspace, 'apphost.mts'), `
        import { createBuilder } from './.aspire/modules/aspire.mjs';
        import { writeFile } from 'node:fs/promises';
        const builder = await createBuilder();
        const pipeline = await builder.pipeline();
        await pipeline.addStep('fail-policy', async () => { throw new Error('Intentional pipeline failure.'); });
        await pipeline.addStep('after-failure', async () => {
            await writeFile('forbidden-after-failure', '');
        }, { dependsOn: ['fail-policy'] });
        await builder.build().run();
    `);
    const failedPipeline = await cli('do', ['after-failure'], environment, 'failure');
    assert.match(failedPipeline.diagnostics, /failed|failure/i);
    await assert.rejects(readFile(join(workspace, 'forbidden-after-failure')), { code: 'ENOENT' });
    assert.equal((await owned('container')).length, 2);

    await writeFile(join(workspace, 'apphost.mts'), `
        import { createBuilder, WellKnownPipelineSteps } from './.aspire/modules/aspire.mjs';
        import assert from 'node:assert/strict';
        import { writeFile } from 'node:fs/promises';
        const builder = await createBuilder();
        await (await builder.pipeline()).addStep('startup-policy', async context => {
            assert.equal(await context.executionContext().isRunMode(), true);
            await writeFile('startup-policy.json', JSON.stringify({ beforeStart: true }));
        }, { requiredBy: [WellKnownPipelineSteps.BeforeStart] });
        const probe = builder.addExecutable('probe', process.execPath, process.cwd()).withArgs('-e',
            "const fs=require('node:fs');if(!JSON.parse(fs.readFileSync('startup-policy.json')).beforeStart)process.exit(1);fs.writeFileSync('run-pipeline-proof','passed')");
        const application = builder.build();
        try {
            await application.start();
            await application.waitForCompletion(probe);
        } finally { await application.dispose(); }
    `);
    await run(cliPath, ['run', '--apphost', 'apphost.mts', '--non-interactive', '--nologo']);
    assert.equal(await readFile(join(workspace, 'run-pipeline-proof'), 'utf8'), 'passed');
    await writeFile(join(workspace, 'proof.json'), JSON.stringify({
        project, first, retained, workspace, customTargets: true, parallelSteps: true, failureStopsDependents: true,
        multipleEnvironments: true, unchangedTypeScriptFixture: true, beforeStart: true, runStartStop: true
    }, null, 2));
} finally {
    if (project && ownsProject) {
        const containers = await owned('container');
        if (containers.length) await run('docker', ['container', 'rm', '--force', ...containers]);
        const networks = await owned('network');
        if (networks.length) await run('docker', ['network', 'rm', ...networks]);
        const volumes = await owned('volume');
        if (volumes.length) await run('docker', ['volume', 'rm', ...volumes]);
        assert.deepEqual(await owned('container'), []);
        assert.deepEqual(await owned('volume'), []);
        assert.deepEqual(await owned('network'), []);
        const images = (await run('docker', ['image', 'ls', image, '--format', '{{.ID}}'])).stdout.trim();
        if (images) await run('docker', ['image', 'rm', image]);
    }
}
console.log(JSON.stringify({ composePublishDeploy: 'passed', workspace, ownedDockerResourcesRemoved: true }));
