#!/usr/bin/env node
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { spawn } from 'node:child_process';
import { constants } from 'node:os';

const [duration, executable, ...args] = process.argv.slice(2);
const match = /^([1-9]\d*)(s|m)$/.exec(duration ?? '');
if (!match || !executable || process.platform !== 'linux') {
    console.error('Usage (Linux): node run-bounded-image-build.ts <positive duration in s or m> <command> [arguments...]');
    process.exit(125);
}

const milliseconds = Number(match[1]) * (match[2] === 'm' ? 60000 : 1000);
if (!Number.isSafeInteger(milliseconds) || milliseconds > 2147483647) {
    console.error(`Invalid image build duration: ${duration}`);
    process.exit(125);
}

// A separate session lets us signal the client and all of its descendants,
// without signaling the workflow shell or other builds on the runner.
const child = spawn(executable, args, { detached: true, stdio: 'inherit' });
let deadlineReached = false;
let cancellationExitCode = 0;
let forcedKill = false;
let deadline: NodeJS.Timeout | undefined;
let grace: NodeJS.Timeout | undefined;

function signalGroup(signal: NodeJS.Signals): void {
    if (child.pid === undefined) {
        return;
    }
    try {
        process.kill(-child.pid, signal);
    } catch (error) {
        // The client may finish between the signal event and the group kill.
        if (!(error instanceof Error && 'code' in error && error.code === 'ESRCH')) {
            throw error;
        }
    }
}

function stopClient(): void {
    signalGroup('SIGTERM');
    grace ??= setTimeout(() => {
        forcedKill = true;
        console.error('Image build did not acknowledge cancellation within 30s; killing its process group');
        signalGroup('SIGKILL');
    }, 30000);
}

function cancel(exitCode: number): void {
    // Keep cancellation independent of the deadline, including signals arriving
    // during the deadline's cleanup grace. GNU timeout returns 124 in that race.
    cancellationExitCode = exitCode;
    console.error('Image build externally cancelled; default-source retry is disabled');
    stopClient();
}

process.on('SIGTERM', () => cancel(143));
process.on('SIGINT', () => cancel(130));
child.once('spawn', () => {
    deadline = setTimeout(() => {
        deadlineReached = true;
        console.error(`Image build exceeded ${duration}; cancelling its process group`);
        stopClient();
    }, milliseconds);
});
child.once('error', error => {
    console.error(`Could not start image build: ${error.message}`);
    process.exit('code' in error && error.code === 'ENOENT' ? 127 : 125);
});
child.once('exit', (code, signal) => {
    clearTimeout(deadline);
    clearTimeout(grace);
    const clientExitCode = code ?? (signal ? 128 + constants.signals[signal] : 1);
    // An external kill/OOM during cleanup is not acknowledged cancellation.
    process.exit(forcedKill || clientExitCode === 137 ? 137 : cancellationExitCode || (deadlineReached ? 124 : clientExitCode));
});
