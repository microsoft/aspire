'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

function runGh(args, input) {
    return new Promise((resolve, reject) => {
        const child = spawn('gh', args, {
            stdio: ['pipe', 'pipe', 'pipe'],
        });
        let stdout = '';
        let stderr = '';
        child.stdout.setEncoding('utf8');
        child.stderr.setEncoding('utf8');
        child.stdout.on('data', chunk => { stdout += chunk; });
        child.stderr.on('data', chunk => { stderr += chunk; });
        child.on('error', reject);
        child.on('close', code => {
            if (code === 0) {
                resolve(stdout);
                return;
            }
            reject(new Error(`gh ${args.join(' ')} failed: ${stderr.trim() || stdout.trim()}`));
        });
        child.stdin.end(input);
    });
}

function parsePaginatedJson(content) {
    const value = JSON.parse(content);
    if (Array.isArray(value[0])) {
        return value.flat();
    }
    return value;
}

function createFixtureTransport(repository, fixtureDirectory) {
    const readFixture = name => JSON.parse(
        fs.readFileSync(path.join(fixtureDirectory, `${name}.json`), 'utf8'));
    const listIssuesPath = path.join(fixtureDirectory, 'list-issues.json');
    const issues = (fs.existsSync(listIssuesPath)
        ? parsePaginatedJson(JSON.stringify(readFixture('list-issues')))
        : [])
        .filter(issue => !issue.pull_request)
        .map(issue => ({ comments: [], ...issue }));

    return {
        ensureLabel: async () => {},
        listIssues: async () => issues,
        createIssue: async request => {
            const response = readFixture('create-issue');
            const issue = {
                ...request,
                number: response.number,
                html_url: response.html_url ?? response.url ??
                    `https://github.com/${repository}/issues/${response.number}`,
                state: 'open',
                comments: [],
            };
            issues.push(issue);
            return issue;
        },
        updateIssue: async (issueNumber, patch) => Object.assign(
            issues.find(issue => issue.number === issueNumber),
            patch),
        addComment: async (issueNumber, body) => {
            readFixture('add-issue-comment');
            issues.find(issue => issue.number === issueNumber).comments.push(body);
        },
        closeIssue: async (issueNumber, { stateReason = 'completed' } = {}) => {
            readFixture('close-issue');
            Object.assign(issues.find(issue => issue.number === issueNumber), {
                state: 'closed',
                state_reason: stateReason,
            });
        },
        reopenIssue: async issueNumber => {
            readFixture('reopen-issue');
            issues.find(issue => issue.number === issueNumber).state = 'open';
        },
        listComments: async issueNumber => {
            const fixtureName = `list-comments-${issueNumber}`;
            const fixturePath = path.join(fixtureDirectory, `${fixtureName}.json`);
            if (fs.existsSync(fixturePath)) {
                return parsePaginatedJson(JSON.stringify(readFixture(fixtureName)));
            }
            return issues.find(issue => issue.number === issueNumber).comments.map(body => ({ body }));
        },
    };
}

function createGhApiIssueTransport(
    repository,
    {
        runGh: invokeGh = runGh,
        fixtureDirectory = process.env.ASPIRE_FAILING_TEST_ISSUE_FIXTURE_DIR,
    } = {}) {
    if (fixtureDirectory) {
        return createFixtureTransport(repository, fixtureDirectory);
    }

    const invokeJson = async (args, body) => {
        const content = await invokeGh(
            ['api', ...args, ...(body === undefined ? [] : ['--input', '-'])],
            body === undefined ? undefined : JSON.stringify(body));
        return content.trim() ? JSON.parse(content) : {};
    };
    const invokePaginated = async endpoint => parsePaginatedJson(
        await invokeGh(['api', '--paginate', '--slurp', endpoint]));
    const issueEndpoint = issueNumber => `repos/${repository}/issues/${issueNumber}`;

    return {
        ensureLabel: async definition => {
            try {
                await invokeJson(
                    ['--method', 'POST', `repos/${repository}/labels`],
                    definition);
            } catch (error) {
                if (!error.message.includes('HTTP 422')) {
                    throw error;
                }
            }
        },
        listIssues: async label => (await invokePaginated(
            `repos/${repository}/issues?labels=${encodeURIComponent(label)}&state=all&per_page=100`))
            .filter(issue => !issue.pull_request),
        createIssue: async request => await invokeJson(
            ['--method', 'POST', `repos/${repository}/issues`],
            request),
        updateIssue: async (issueNumber, patch) => await invokeJson(
            ['--method', 'PATCH', issueEndpoint(issueNumber)],
            patch),
        addComment: async (issueNumber, body) => {
            await invokeJson(
                ['--method', 'POST', `${issueEndpoint(issueNumber)}/comments`],
                { body });
        },
        closeIssue: async (issueNumber, { stateReason = 'completed' } = {}) => {
            await invokeJson(
                ['--method', 'PATCH', issueEndpoint(issueNumber)],
                { state: 'closed', state_reason: stateReason });
        },
        reopenIssue: async issueNumber => {
            await invokeJson(
                ['--method', 'PATCH', issueEndpoint(issueNumber)],
                { state: 'open' });
        },
        listComments: async issueNumber => await invokePaginated(
            `${issueEndpoint(issueNumber)}/comments?per_page=100`),
    };
}

module.exports = {
    createGhApiIssueTransport,
};
