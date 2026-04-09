const fs = require('node:fs/promises');
const helper = require('../../../.github/workflows/create-failing-test-issue.js');

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
        case 'parseCommand':
            return helper.parseCommand(payload.body, payload.defaultSourceUrl ?? null);

        case 'buildIssueSearchQuery':
            return helper.buildIssueSearchQuery(payload.owner ?? 'microsoft', payload.repo ?? 'aspire', payload.metadataMarker);

        case 'formatListResponse':
            return helper.formatListResponse(payload.resolverOutcome, payload.resultJson ?? null);

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
