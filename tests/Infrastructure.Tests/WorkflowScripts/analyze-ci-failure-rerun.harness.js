// Test harness for the rerun_failed_jobs safe-output handler compiled into
// .github/workflows/analyze-ci-failure.lock.yml.
const fs = require('node:fs/promises');

async function main() {
    const inputPath = process.argv[2];
    const outputPath = process.argv[3];
    if (!inputPath || !outputPath) {
        throw new Error('Expected input and output file paths.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    process.env.GH_AW_AGENT_OUTPUT = request.agentOutputPath;
    process.env.ENABLE_RERUN = request.enableRerun ?? 'true';

    const calls = { failed: [], reruns: [], infos: [], warnings: [] };
    const github = {
        rest: {
            pulls: {
                get: async () => ({ data: { state: request.prState ?? 'open' } }),
            },
            actions: {
                getWorkflowRun: async () => ({ data: { run_attempt: request.currentRunAttempt ?? 1 } }),
                reRunWorkflowFailedJobs: async args => { calls.reruns.push(args.run_id); },
            },
        },
    };
    const context = {
        repo: { owner: 'microsoft', repo: 'aspire' },
    };
    const core = {
        setFailed: message => { calls.failed.push(String(message)); },
        info: message => { calls.infos.push(String(message)); },
        warning: message => { calls.warnings.push(String(message)); },
    };

    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    const run = new AsyncFunction('require', 'process', 'github', 'context', 'core', request.script);
    await run(require, process, github, context, core);

    await fs.writeFile(outputPath, JSON.stringify({
        Failed: calls.failed,
        Reruns: calls.reruns,
        Infos: calls.infos,
        Warnings: calls.warnings,
    }));
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
