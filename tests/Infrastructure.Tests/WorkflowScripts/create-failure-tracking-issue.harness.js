const fs = require('node:fs/promises');
const helper = require('../../../.github/workflows/create-failure-tracking-issue.js');

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
        case 'buildDedupQuery':
            return helper.buildDedupQuery(payload.owner, payload.repo, payload.title);

        case 'createOrCommentOnFailureIssue':
            return await simulateCreateOrComment(payload);

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

// Drives createOrCommentOnFailureIssue against a fake Octokit so the search /
// comment / create branches can be exercised without touching GitHub.
async function simulateCreateOrComment(payload) {
    const calls = { search: [], comment: [], create: [], warnings: [] };
    const fakeCore = {
        warning(msg) { calls.warnings.push(msg); },
        info() {},
    };

    const items = payload.searchResults ?? [];
    const throwOnSearch = payload.searchThrows === true;

    const fakeGithub = {
        rest: {
            search: {
                issuesAndPullRequests: async ({ q, per_page }) => {
                    calls.search.push({ q, perPage: per_page });
                    if (throwOnSearch) {
                        throw new Error('simulated search failure');
                    }
                    return { data: { items } };
                },
            },
            issues: {
                createComment: async ({ owner, repo, issue_number, body }) => {
                    calls.comment.push({ owner, repo, issueNumber: issue_number, body });
                    return { data: { id: 9999 } };
                },
                create: async ({ owner, repo, title, body, labels }) => {
                    calls.create.push({ owner, repo, title, body, labels });
                    return { data: { html_url: 'https://example.invalid/issues/42', number: 42 } };
                },
            },
        },
    };

    const result = await helper.createOrCommentOnFailureIssue({
        github: fakeGithub,
        core: fakeCore,
        owner: payload.owner,
        repo: payload.repo,
        title: payload.title,
        body: payload.body,
        labels: payload.labels,
        runUrl: payload.runUrl,
    });

    return { result, calls };
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
