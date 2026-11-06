// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const mirrorArg = 'UBUNTU_APT_MIRROR=http://azure.archive.ubuntu.com/ubuntu/';

function isAlive(pid: number): boolean {
    try {
        process.kill(pid, 0);
        // /proc/PID/stat starts "123 (sleep) S ...". A killed orphan can briefly
        // remain as a zombie ('Z') until init reaps it.
        return fs.readFileSync(`/proc/${pid}/stat`, 'utf8').split(') ')[1][0] !== 'Z';
    } catch (error) {
        if (error instanceof Error && 'code' in error && (error.code === 'ESRCH' || error.code === 'ENOENT')) {
            return false;
        }
        throw error;
    }
}

function stubDocker(): void {
    const root = process.env.IMAGE_BUILD_TEST_ROOT!;
    const scenario = process.env.IMAGE_BUILD_TEST_SCENARIO!;
    const args = process.argv.slice(3);
    fs.appendFileSync(path.join(root, 'trace'), JSON.stringify({ args, buildkit: process.env.DOCKER_BUILDKIT }) + '\n');
    if (!args.includes('build')) {
        return;
    }
    if (!args.includes(mirrorArg)) {
        if (scenario === 'stall' || scenario === 'slow-success') {
            const childPid = Number(fs.readFileSync(path.join(root, 'child-pid'), 'utf8'));
            assert.equal(isAlive(childPid), false, 'Fallback started before primary child stopped');
            assert.equal(fs.readFileSync(path.join(root, 'cancelled'), 'utf8'), 'acknowledged');
        }
        console.log('default sources output');
        console.error('default sources diagnostic');
        process.exit(scenario === 'fallback-failure' ? 42 : 0);
    }

    console.log('primary mirror output');
    console.error('primary mirror diagnostic');
    fs.writeFileSync(path.join(root, 'primary-pid'), String(process.pid));
    switch (scenario) {
        case 'success':
            return;
        case 'cancelled':
            process.kill(process.pid, 'SIGTERM');
            return;
        case 'external-cancel':
        case 'cleanup-cancel': {
            let stopping = false;
            process.on('SIGTERM', () => {
                if (stopping) {
                    return;
                }
                stopping = true;
                if (scenario === 'cleanup-cancel') {
                    console.error('Deadline cleanup started; sending external TERM to supervisor');
                    process.kill(process.ppid, 'SIGTERM');
                }
                setTimeout(() => {
                    console.error('Client handled external TERM and exited 1');
                    process.exit(1);
                }, 100);
            });
            if (scenario === 'external-cancel') {
                process.kill(process.ppid, 'SIGTERM');
            }
            setInterval(() => {}, 1000);
            return;
        }
        case 'stall':
        case 'slow-success':
        case 'forced-kill': {
            // A real descendant, not a mocked timeout, must stop before retry.
            const child = spawn('bash', ['-c', `
                ${scenario === 'forced-kill' ? "trap '' TERM" : ''}
                echo "$$" > "$IMAGE_BUILD_TEST_ROOT/child-pid"
                exec sleep 300
            `], { stdio: 'inherit' });
            if (scenario === 'forced-kill') {
                process.on('SIGTERM', () => {});
            } else {
                process.on('SIGTERM', () => {
                    child.once('exit', () => {
                        fs.writeFileSync(path.join(root, 'cancelled'), 'acknowledged');
                        process.exit(143);
                    });
                    if (child.exitCode !== null || child.signalCode !== null) {
                        fs.writeFileSync(path.join(root, 'cancelled'), 'acknowledged');
                        process.exit(143);
                    }
                });
            }
            if (scenario === 'slow-success') {
                setTimeout(() => process.exit(0), 30000);
            } else {
                setInterval(() => {}, 1000);
            }
            return;
        }
        default:
            process.exit(17);
    }
}

function testBuild(repo: string, scenario: string): void {
    const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-image-build-'));
    try {
        const bin = path.join(workspace, 'commands with spaces');
        fs.mkdirSync(bin);
        fs.writeFileSync(path.join(bin, 'docker'), `#!/usr/bin/env bash
exec "$IMAGE_BUILD_TEST_NODE" "$IMAGE_BUILD_TEST_HARNESS" --stub "$@"
`, { mode: 0o755 });
        const script = path.join(repo, 'eng', 'scripts', 'build-cli-e2e-images.sh');
        const orchestration = scenario === 'all-images' || scenario === 'dotnet-only';
        const started = Date.now();
        const result = spawnSync('bash', orchestration
            ? [script]
            : ['-c', 'source "$1"; build_with_mirror_retry 5s "test image" docker build -f "Dockerfile with spaces" -t test:primary', 'bash', script], {
            env: {
                ...process.env,
                PATH: `${bin}${path.delimiter}${process.env.PATH}`,
                IMAGE_BUILD_TEST_NODE: process.execPath,
                IMAGE_BUILD_TEST_HARNESS: fileURLToPath(import.meta.url),
                IMAGE_BUILD_TEST_ROOT: workspace,
                IMAGE_BUILD_TEST_SCENARIO: scenario,
                CLI_E2E_INCLUDE_POLYGLOT_IMAGES: scenario === 'all-images' ? 'true' : 'false',
                CLI_E2E_DOTNET_IMAGE_TAG: 'test:dotnet',
                CLI_E2E_POLYGLOT_BASE_IMAGE_TAG: 'test:base',
                CLI_E2E_POLYGLOT_IMAGE_TAG: 'test:polyglot',
                CLI_E2E_POLYGLOT_JAVA_IMAGE_TAG: 'test:java',
                CLI_E2E_POLYGLOT_GO_IMAGE_TAG: 'test:go'
            },
            encoding: 'utf8',
            timeout: 60000,
            killSignal: 'SIGKILL'
        });
        assert.ifError(result.error);
        const output = result.stdout + result.stderr;
        const expectedStatus = scenario === 'fallback-failure' ? 42
            : scenario === 'forced-kill' ? 137
            : ['cancelled', 'external-cancel', 'cleanup-cancel'].includes(scenario) ? 143 : 0;
        assert.equal(result.status, expectedStatus, output);
        const trace = fs.readFileSync(path.join(workspace, 'trace'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        if (orchestration) {
            const builds = trace.filter(entry => entry.args.includes('build'));
            const variants = scenario === 'all-images' ? ['dotnet', 'base', 'java', 'go'] : ['dotnet'];
            assert.equal(builds.length, variants.length * 2, output);
            for (const [index, variant] of variants.entries()) {
                const primary = builds[index * 2];
                const fallback = builds[index * 2 + 1];
                assert.equal(primary.args[primary.args.indexOf('-t') + 1], `test:${variant}`);
                assert.deepEqual(primary.args.slice(-3), ['--build-arg', mirrorArg, '.']);
                assert.deepEqual(fallback.args, [...primary.args.slice(0, -3), '.']);
                if (variant === 'java' || variant === 'go') {
                    assert.equal(primary.buildkit, '1');
                    assert.equal(fallback.buildkit, '1');
                } else {
                    assert.ok(primary.args.includes('--load'));
                    assert.ok(primary.args.includes('SKIP_SOURCE_BUILD=true'));
                    assert.ok(primary.args.includes(`type=gha,scope=cli-e2e-${variant === 'dotnet' ? 'dotnet' : 'polyglot-base'}`));
                }
            }
            assert.deepEqual(trace.filter(entry => !entry.args.includes('build')).map(entry => entry.args), [
                ['buildx', 'create', '--name', 'cli-e2e-builder', '--use'],
                ['buildx', 'inspect', '--bootstrap'],
                ...(scenario === 'all-images' ? [['tag', 'test:base', 'test:polyglot']] : [])
            ]);
        } else {
            const expectedArgs = ['build', '-f', 'Dockerfile with spaces', '-t', 'test:primary'];
            const noFallback = ['success', 'forced-kill', 'cancelled', 'external-cancel', 'cleanup-cancel'].includes(scenario);
            assert.deepEqual(trace.map(entry => entry.args), [
                [...expectedArgs, '--build-arg', mirrorArg, '.'],
                ...(noFallback ? [] : [[...expectedArgs, '.']])
            ]);
            assert.ok(output.includes('primary mirror output'), output);
            assert.ok(output.includes('primary mirror diagnostic'), output);
            if (!noFallback) {
                assert.ok(output.includes('default sources output'), output);
                assert.ok(output.includes('default sources diagnostic'), output);
            }
            if (scenario === 'stall' || scenario === 'slow-success') {
                assert.match(output, /exceeded 5s; retrying with default Ubuntu apt sources/);
                assert.ok(Date.now() - started < 15000, output);
            }
            if (['forced-kill', 'cancelled', 'external-cancel', 'cleanup-cancel'].includes(scenario)) {
                assert.match(output, /not retrying after cancellation or timeout execution failure/);
            }
            if (scenario === 'external-cancel' || scenario === 'cleanup-cancel') {
                assert.match(output, /Client handled external TERM and exited 1/);
            }
            if (scenario === 'cleanup-cancel') {
                assert.match(output, /Deadline cleanup started; sending external TERM to supervisor/);
                assert.match(output, /Image build exceeded 5s/);
            }
        }
        const childPath = path.join(workspace, 'child-pid');
        assert.equal(isAlive(Number(fs.readFileSync(path.join(workspace, 'primary-pid'), 'utf8'))), false, output);
        if (fs.existsSync(childPath)) {
            assert.equal(isAlive(Number(fs.readFileSync(childPath, 'utf8'))), false, output);
        }
        console.log(output);
        console.log(`Passed: ${scenario}`);
    } finally {
        for (const name of ['primary-pid', 'child-pid']) {
            const pidPath = path.join(workspace, name);
            if (fs.existsSync(pidPath)) {
                const pid = Number(fs.readFileSync(pidPath, 'utf8'));
                if (isAlive(pid)) {
                    process.kill(pid, 'SIGKILL');
                }
            }
        }
        fs.rmSync(workspace, { recursive: true, force: true });
    }
}

function testInheritedSources(repo: string): void {
    const script = path.join(repo, 'tests', 'Shared', 'Docker', 'configure-ubuntu-apt-mirror.sh');
    const result = spawnSync('docker', [
        'run', '--rm', '--interactive',
        'mcr.microsoft.com/mirror/docker/library/ubuntu:24.04', 'sh', '-ec', `
            cat > /configure-mirror.sh
            cp /etc/apt/sources.list.d/ubuntu.sources /tmp/original.sources
            echo 'deb https://packages.example.invalid stable main' > /etc/apt/sources.list.d/third-party.list
            for legacy in absent present; do
                rm -rf /etc/apt/aspire-default-ubuntu-sources
                if [ "$legacy" = present ]; then
                    echo '# original legacy sources' > /etc/apt/sources.list
                else
                    rm -f /etc/apt/sources.list
                fi
                sh /configure-mirror.sh http://azure.archive.ubuntu.com/ubuntu/
                grep -q 'http://azure.archive.ubuntu.com/ubuntu' /etc/apt/sources.list.d/ubuntu.sources
                # The derived layer must not overwrite the base layer's snapshot.
                sh /configure-mirror.sh https://different.example.invalid/ubuntu
                sh /configure-mirror.sh ""
                cmp /tmp/original.sources /etc/apt/sources.list.d/ubuntu.sources
                if [ "$legacy" = present ]; then
                    test "$(cat /etc/apt/sources.list)" = '# original legacy sources'
                else
                    test ! -f /etc/apt/sources.list
                fi
                test "$(cat /etc/apt/sources.list.d/third-party.list)" = 'deb https://packages.example.invalid stable main'
                sh /configure-mirror.sh ""
                cmp /tmp/original.sources /etc/apt/sources.list.d/ubuntu.sources
            done
        `
    ], { encoding: 'utf8', timeout: 90000, input: fs.readFileSync(script) });
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    console.log('Passed: inherited-sources (base/derived configuration, original legacy absence/presence and third-party sources)');
}

if (process.argv[2] === '--stub') {
    stubDocker();
} else if (process.argv[3] === 'inherited-sources') {
    testInheritedSources(process.argv[2]);
} else {
    testBuild(process.argv[2], process.argv[3]);
}
