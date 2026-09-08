const fs = require('node:fs/promises');

async function main() {
    const inputPath = process.argv[2];
    const outputPath = process.argv[3];
    if (!inputPath || !outputPath) {
        throw new Error('Expected input and output file paths.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    const labels = new Set(request.pullRequest.labels.map(label => label.name));
    const calls = [];

    const github = {
        rest: {
            pulls: {
                get: async () => {
                    calls.push('pulls.get');
                    return { data: request.pullRequest };
                },
            },
            issues: {
                addLabels: async ({ labels: addedLabels }) => {
                    calls.push('addLabels');
                    for (const label of addedLabels) {
                        labels.add(label);
                    }
                },
                removeLabel: async ({ name }) => {
                    calls.push('removeLabel');
                    if (request.removeLabelNotFound) {
                        labels.delete(name);
                        const error = new Error('Not Found');
                        error.status = 404;
                        throw error;
                    }
                    if (!labels.delete(name)) {
                        const error = new Error('Not Found');
                        error.status = 404;
                        throw error;
                    }
                },
            },
        },
    };
    const context = {
        repo: { owner: 'microsoft', repo: 'aspire' },
        payload: { pull_request: { number: 19893 } },
    };

    // github-script wraps this block in an async function and provides github/context as arguments.
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    const run = new AsyncFunction('github', 'context', request.script);
    await run(github, context);

    await fs.writeFile(outputPath, JSON.stringify({
        result: {
            labels: [...labels].sort(),
            calls,
        },
    }));
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
