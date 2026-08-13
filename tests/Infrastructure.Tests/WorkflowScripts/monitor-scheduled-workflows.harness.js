const fs = require('node:fs/promises');
const helper = require('../../../.github/workflows/monitor-scheduled-workflows.js');

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
            return helper.buildMarker(payload.workflowFile);

        case 'buildIssueTitle':
            return helper.buildIssueTitle(payload.displayName);

        case 'decideAction':
            return helper.decideAction({
                conclusion: payload.conclusion ?? null,
                issue: payload.issue ?? null,
                failureConclusions: payload.selfReports ? helper.BACKSTOP_CONCLUSIONS : undefined,
            });

        case 'selectEnabled':
            return helper.selectEnabled(payload.config);

        case 'buildIssueBody':
            return helper.buildIssueBody({
                marker: payload.marker,
                displayName: payload.displayName,
                workflowFile: payload.workflowFile,
                selfReports: payload.selfReports === true,
            });

        case 'formatComment':
            return helper.formatComment({
                runUrl: payload.runUrl ?? null,
                runNumber: payload.runNumber ?? null,
                sha: payload.sha ?? null,
                conclusion: payload.conclusion,
            });

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
