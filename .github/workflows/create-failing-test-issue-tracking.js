'use strict';

const tracking = require('./tracking-issue.js');
const { createGhApiIssueTransport } = require('./tracking-issue-gh-api.js');

async function reconcile({ transport, core, issue, runId, forceNew = false }) {
    await transport.ensureLabel({
        name: 'failing-test',
        color: 'b60205',
        description: 'A test is failing in CI',
    });

    let result;
    if (forceNew) {
        result = await tracking.executeIssueReconciliation(transport, core, {
            label: 'failing-test',
            labels: issue.labels,
            marker: issue.metadataMarker,
            title: issue.title,
            buildBody: () => `${issue.body}\n\n${tracking.duplicateExemptStamp()}`,
            forceCreate: true,
            issues: [],
        });
    } else {
        result = await tracking.reconcileRun(transport, core, {
            label: 'failing-test',
            labels: issue.labels,
            marker: issue.metadataMarker,
            title: issue.title,
            runId,
            buildBody: () => issue.body,
            comment: issue.commentBody,
            closeDuplicates: true,
        });
    }

    const previousState = result.plan?.matches?.[0]?.state ?? null;
    const action = result.created
        ? 'created'
        : result.reopened
            ? 'reopened'
            : result.skipped
                ? 'found'
                : 'updated';
    return {
        number: result.number,
        action,
        created: result.created,
        reopened: result.reopened ?? false,
        skipped: result.skipped ?? false,
        duplicatesClosed: result.duplicatesClosed ?? [],
        previousState,
    };
}

async function runCli() {
    let input = '';
    for await (const chunk of process.stdin) {
        input += chunk;
    }

    const request = JSON.parse(input);
    const logs = [];
    const result = await reconcile({
        transport: createGhApiIssueTransport(request.repository),
        core: { info: message => logs.push(String(message)) },
        issue: request.issue,
        runId: request.runId,
        forceNew: request.forceNew === true,
    });
    process.stdout.write(JSON.stringify({
        ...result,
        url: `https://github.com/${request.repository}/issues/${result.number}`,
        logs,
    }));
}

module.exports = {
    reconcile,
};

if (require.main === module) {
    runCli().catch(error => {
        process.stderr.write(`${error.stack ?? error}\n`);
        process.exitCode = 1;
    });
}
