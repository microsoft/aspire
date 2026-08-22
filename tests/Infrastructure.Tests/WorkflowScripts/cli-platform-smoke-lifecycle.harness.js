'use strict';

const fs = require('fs');
const path = require('path');
const Module = require('module');

const operation = process.argv[2];
const diagnosticsDir = process.argv[3];
const ptys = [];

if (operation === 'startup-disposal') {
    const originalSetTimeout = global.setTimeout;
    global.setTimeout = (callback, delay, ...args) =>
        originalSetTimeout(callback, delay === 10_000 ? 20 : delay, ...args);
}

const originalLoad = Module._load;
Module._load = function (request) {
    if (request === 'node-pty') {
        return {
            spawn() {
                const pty = new FakePty();
                ptys.push(pty);
                return pty;
            }
        };
    }

    return originalLoad.apply(this, arguments);
};

const { runScenario } = require('../../../eng/scripts/cli-platform-smoke/lib/run-scenario');
Module._load = originalLoad;

class FakePty {
    constructor() {
        this.command = '';
        this.commandCount = 0;
        this.dataCallback = null;
        this.killCount = 0;
    }

    onData(callback) {
        this.dataCallback = callback;
    }

    write(data) {
        if (data !== '\r') {
            this.command = data;
            return;
        }

        this.commandCount++;
        if (this.commandCount === 1 && operation !== 'startup-disposal') {
            // ShellSession wraps each command with a completion marker such as:
            //   echo __ASPIRE_SMOKE_READY__; printf '\n__ASPIRE_SMOKE_DONE_1234_abcd__:%s\n' $?
            const sentinel = /__ASPIRE_SMOKE_DONE_\d+_[0-9a-f]+__/.exec(this.command)?.[0];
            if (!sentinel) {
                throw new Error(`Could not find the completion sentinel in '${this.command}'.`);
            }

            queueMicrotask(() => this.dataCallback(`__ASPIRE_SMOKE_READY__\n${sentinel}:0\n`));
        }
    }

    kill() {
        this.killCount++;
    }
}

async function main() {
    let callbackSettled = false;
    const description = operation.replaceAll('-', ' ');
    const context = {
        aspireCommand: 'aspire',
        diagnosticsDir,
        projectRoot: diagnosticsDir
    };
    const scenario = {
        description,
        timeoutMs: operation === 'callback-settlement' ? 150 : 1_000,
        callback: async controller => {
            await controller.runAspireCommand(['version'], {
                timeoutMs: operation === 'command-timeout' ? 20 : 1_000
            });

            if (operation === 'callback-settlement') {
                try {
                    await controller.waitFor('never emitted', 'waiting for fake output', 1_000);
                } catch (error) {
                    await new Promise(resolve => setTimeout(resolve, 40));
                    callbackSettled = true;
                    throw error;
                }
            }
        }
    };

    let errorMessage = null;
    try {
        await runScenario(scenario, context);
    } catch (error) {
        errorMessage = error.message;
    }

    process.stdout.write(JSON.stringify({
        callbackSettled,
        castExists: fs.existsSync(path.join(diagnosticsDir, `${operation}.cast`)),
        errorMessage,
        killCount: ptys.reduce((count, pty) => count + pty.killCount, 0),
        logExists: fs.existsSync(path.join(diagnosticsDir, `${operation}.log`))
    }));
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
