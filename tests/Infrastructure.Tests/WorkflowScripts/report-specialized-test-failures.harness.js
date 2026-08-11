const fs = require('node:fs/promises');
const helper = require('../../../.github/workflows/report-specialized-test-failures.js');

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
            return helper.buildMarker(payload.workflowFile, payload.kind);

        case 'labelForKind':
            return helper.labelForKind(payload.kind);

        case 'buildIssueTitle':
            return helper.buildIssueTitle(payload.displayName, payload.kind);

        case 'classifyFailure':
            return helper.classifyFailure({
                result: payload.result,
                failedCount: payload.failedCount ?? 0,
                ignoreTestFailures: payload.ignoreTestFailures ?? false,
                extractionFailed: payload.extractionFailed ?? false,
            });

        case 'buildIssueBody':
            return helper.buildIssueBody({
                marker: payload.marker,
                displayName: payload.displayName,
                workflowFile: payload.workflowFile,
                kind: payload.kind,
            });

        case 'formatComment':
            return helper.formatComment({
                kind: payload.kind,
                run: payload.run,
                failedTests: payload.failedTests ?? [],
                maxListed: payload.maxListed,
            });

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
