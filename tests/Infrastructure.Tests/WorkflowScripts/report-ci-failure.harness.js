const fs = require('node:fs/promises');
const helper = require('../../../.github/workflows/report-ci-failure.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    const result = await dispatch(request.operation, request.payload ?? {});
    process.stdout.write(JSON.stringify({ result }));
}

async function dispatch(operation, payload) {
    switch (operation) {
        case 'buildMarker':
            return helper.buildMarker(payload.ref);

        case 'buildIssueTitle':
            return helper.buildIssueTitle(payload.ref);

        case 'buildIssueBody':
            return helper.buildIssueBody({ marker: payload.marker, ref: payload.ref });

        case 'formatComment':
            return helper.formatComment({ run: payload.run });

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
