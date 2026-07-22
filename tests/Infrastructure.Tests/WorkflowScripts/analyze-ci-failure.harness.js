const fs = require('node:fs/promises');
const helper = require('../../../.github/workflows/analyze-ci-failure.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    const result = dispatch(request.operation, request.payload ?? {});
    process.stdout.write(JSON.stringify({ result }));
}

function dispatch(operation, payload) {
    switch (operation) {
        case 'addOccurrence':
            return helper.addOccurrence(payload.analysis, payload.cause);

        case 'buildIssueBody':
            return helper.buildIssueBody(payload.analysis, payload.cause, payload.marker);

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});