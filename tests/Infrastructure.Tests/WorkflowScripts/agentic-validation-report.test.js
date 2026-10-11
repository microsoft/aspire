// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

const assert = require('node:assert/strict');
const { after, test } = require('node:test');
const { mkdirSync, mkdtempSync, rmSync, writeFileSync, unlinkSync, symlinkSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { execFileSync } = require('node:child_process');
const report = require('../../../.github/workflows/agentic-validation-report.js');

const temporaryDirectories = [];
after(() => {
    for (const path of temporaryDirectories) rmSync(path, { recursive: true, force: true });
});

const stageNames = [
    'Checkout validation sources', 'Install gh-aw extension (v0.89.17)',
    'Compile agentic workflows (schema and action-pin validation)', 'Verify generated files are up to date',
    'Lint generated agentic workflows', 'Set up .NET SDK', 'Restore',
    'Run Infrastructure.Tests agentic workflow contracts',
];

function fixture() {
    const run = {
        id: 100, workflow_id: 10, run_number: 5, run_attempt: 1, status: 'completed', conclusion: 'failure',
        repository: { id: 1 }, path: '.github/workflows/validate-agentic-workflows.yml', event: 'pull_request',
        head_repository: { id: 2, owner: { login: 'contributor' } }, head_branch: 'feature',
        head_sha: 'a'.repeat(40), pull_requests: [{ number: 42 }],
    };
    const pr = { number: 42, state: 'open', base: { repo: { id: 1 } },
        head: { repo: { id: 2 }, ref: 'feature', sha: run.head_sha } };
    const context = {
        repo: { owner: 'microsoft', repo: 'aspire' }, serverUrl: 'https://github.com', runId: 100,
        payload: { workflow_run: structuredClone(run), repository: { id: 1 } },
    };
    const state = {
        run, pr, context, workflowPath: run.path, runs: [run], candidates: [pr], comments: [],
        jobs: [{ steps: [
            { name: 'Install gh-aw extension (v0.89.17)', conclusion: 'success' },
            { name: 'Verify generated files are up to date', conclusion: 'failure' },
        ] }],
        created: [], updated: [], warnings: [], summaries: [], calls: [],
    };
    const endpoint = name => Object.assign(() => {}, { endpoint: name });
    const github = {
        rest: {
            actions: {
                getWorkflowRun: async () => ({ data: state.run }),
                getWorkflow: async () => ({ data: { path: state.workflowPath } }),
                listWorkflowRuns: endpoint('runs'),
            },
            pulls: {
                get: async () => ({ data: state.pr }),
                list: endpoint('candidates'),
            },
            issues: {
                listComments: endpoint('comments'),
                createComment: async args => {
                    if (state.error) throw state.error;
                    state.created.push(args);
                    state.comments.push({ id: 7, body: args.body, user: { login: 'github-actions[bot]', type: 'Bot' } });
                },
                updateComment: async args => {
                    if (state.error) throw state.error;
                    state.updated.push(args);
                    state.comments.find(comment => comment.id === args.comment_id).body = args.body;
                },
            },
        },
        paginate: async (route, args) => {
            const name = typeof route === 'string' ? 'jobs' : route.endpoint;
            state.calls.push({ name, args });
            if (name === 'comments' && state.beforeWrite) state.beforeWrite();
            if (name === 'jobs') {
                assert.equal(route, 'GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs');
                assert.equal(args.attempt_number, context.payload.workflow_run.run_attempt);
            }
            return state[name];
        },
    };
    const core = {
        warning: message => state.warnings.push(message),
        summary: {
            addRaw(body) { state.summaries.push(body); return this; },
            async write() {},
        },
    };
    return Object.assign(state, { github, core, publish: () => report.publish({ github, core, context }) });
}

test('failure creates a bounded actionable comment only on the triggering fork PR', async () => {
    const f = fixture();
    await f.publish();
    assert.equal(f.created.length, 1);
    assert.equal(f.created[0].issue_number, 42);
    assert.match(f.created[0].body, /^\[automated\] /);
    assert.match(f.created[0].body, /gh aw compile --purge --force-refresh-action-pins --validate --no-check-update/);
    assert.match(f.created[0].body, /Configured compiler: v0\.89\.17/);
    assert.match(f.created[0].body, /runs\/100\/attempts\/1/);
    assert.ok(Buffer.byteLength(f.created[0].body) <= report.limits.comment);
    assert.deepEqual([...new Set(f.calls.map(call => call.name))].sort(), ['comments', 'jobs', 'runs']);
});

test('reruns update one comment and success resolves it without posting again', async () => {
    const f = fixture();
    await f.publish();
    f.run.run_attempt = f.context.payload.workflow_run.run_attempt = 2;
    await f.publish();
    f.run.conclusion = 'success';
    f.jobs = [{ steps: stageNames.map(name => ({ name, conclusion: 'success' })) }];
    await f.publish();
    assert.equal(f.created.length, 1);
    assert.equal(f.updated.length, 2);
    assert.deepEqual(f.updated.map(update => update.comment_id), [7, 7]);
    assert.match(f.updated[1].body, /validation is resolved/);
});

test('success with no previous failure creates no comment', async () => {
    const f = fixture();
    f.run.conclusion = 'success';
    f.jobs = [{ steps: stageNames.map(name => ({ name, conclusion: 'success' })) }];
    await f.publish();
    assert.deepEqual([f.created, f.updated], [[], []]);
});

test('matching marker from another author is not edited, even when it is first', async () => {
    const f = fixture();
    await f.publish();
    f.comments.unshift({ ...f.comments[0], id: 6, user: { login: 'contributor', type: 'User' } });
    await f.publish();
    assert.equal(f.created.length, 1);
    assert.equal(f.updated[0].comment_id, 7);
    assert.equal(f.calls.find(call => call.name === 'comments').args.per_page, 100);
});

for (const [name, change] of [
    ['wrong repository', f => f.run.repository.id = 99],
    ['wrong path', f => f.run.path = '.github/workflows/other.yml'],
    ['wrong workflow ID', f => f.run.workflow_id++],
    ['wrong workflow identity', f => f.workflowPath = '.github/workflows/other.yml'],
    ['deleted source repository', f => f.run.head_repository = null],
    ['missing source branch', f => f.run.head_branch = null],
    ['invalid source SHA', f => f.run.head_sha = 'not-a-commit'],
    ['non-PR event', f => f.run.event = 'push'],
    ['cancelled validation', f => f.run.conclusion = 'cancelled'],
    ['skipped validation', f => f.run.conclusion = 'skipped'],
    ['in-progress validation', f => f.run.status = 'in_progress'],
    ['superseded attempt', f => f.run.run_attempt++],
    ['closed PR', f => f.pr.state = 'closed'],
    ['changed head SHA', f => f.pr.head.sha = 'b'.repeat(40)],
    ['wrong base repository', f => f.pr.base.repo.id = 3],
    ['wrong head repository', f => f.pr.head.repo.id = 3],
    ['wrong head branch', f => f.pr.head.ref = 'unrelated'],
    ['newer run still running', f => f.runs.push({ ...f.run, id: 101, run_number: 6, status: 'in_progress' })],
    ['newer attempt still running', f => f.runs = [{ ...f.run, run_attempt: 2, status: 'in_progress' }]],
    ['run missing from API list', f => f.runs = []],
    ['head changes immediately before write', f => f.beforeWrite = () => f.pr.head.sha = 'b'.repeat(40)],
    ['newer run appears immediately before write', f => f.beforeWrite = () => f.runs.push({ ...f.run, id: 101, run_number: 6 })],
]) {
    test(`${name} does not publish or resolve`, async () => {
        const f = fixture();
        change(f);
        await f.publish();
        assert.deepEqual([f.created, f.updated], [[], []]);
        assert.equal(f.warnings.length, 1);
        assert.match(f.summaries[0], /not published/);
    });
}

test('fork with missing association uses exact live API metadata', async () => {
    const f = fixture();
    f.run.pull_requests = [];
    f.candidates.unshift({ ...f.pr, number: 88, head: { ...f.pr.head, repo: { id: 8 } } });
    await f.publish();
    assert.equal(f.created[0].issue_number, 42);
    assert.equal(f.calls.find(call => call.name === 'candidates').args.head, 'contributor:feature');
});

for (const count of [0, 2]) {
    test(`missing association with ${count} matching PRs fails closed`, async () => {
        const f = fixture();
        f.run.pull_requests = [];
        f.candidates = Array.from({ length: count }, (_, i) => ({ ...f.pr, number: 42 + i }));
        await f.publish();
        assert.deepEqual([f.created, f.updated], [[], []]);
        assert.equal(f.warnings.length, 1);
    });
}

test('unrelated fork run does not suppress reporting for this PR', async () => {
    const f = fixture();
    f.runs.push({ ...f.run, id: 101, run_number: 6, head_repository: { id: 99 } });
    await f.publish();
    assert.equal(f.created.length, 1);
});

for (const [title, advice] of [
    ['Checkout validation sources', /checkout error/],
    ['Install gh-aw extension (v0.89.17)', /Compilation has not run/],
    ['Compile agentic workflows (schema and action-pin validation)', /compiler diagnostics/],
    ['Lint generated agentic workflows', /gh aw lint --shellcheck/],
    ['Set up .NET SDK', /SDK setup error/],
    ['Restore', /restore error/],
    ['Run Infrastructure.Tests agentic workflow contracts', /failing contract or build diagnostic/],
    ['<script>evil</script> ' + 'x'.repeat(10000), /No recognized failed stage/],
]) {
    test(`stage-specific advice: ${title.slice(0, 60)}`, async () => {
        const f = fixture();
        f.jobs = [{ steps: [{ name: title, conclusion: 'failure' }] }];
        await f.publish();
        assert.match(f.created[0].body, advice);
        assert.ok(Buffer.byteLength(f.created[0].body) <= report.limits.comment);
        assert.equal(f.created[0].body.includes('<script>'), false);
        assert.equal(f.created[0].body.includes('gh aw compile --purge'), false);
    });
}

test('job-level failure with no steps remains reportable', async () => {
    const f = fixture();
    f.jobs = [{ conclusion: 'failure' }];
    await f.publish();
    assert.match(f.created[0].body, /No recognized failed stage/);
    assert.match(f.created[0].body, /Configured compiler: unavailable/);
});

test('multiple recognized failures remain within the comment budget', async () => {
    const f = fixture();
    f.jobs = [{ steps: stageNames.map(name => ({ name, conclusion: 'failure' })) }];
    await f.publish();
    assert.ok(Buffer.byteLength(f.created[0].body) <= report.limits.comment);
});

test('a green run with skipped validation stages does not resolve an existing failure', async () => {
    const f = fixture();
    await f.publish();
    f.run.conclusion = 'success';
    f.jobs = [{ steps: stageNames.map(name => ({ name, conclusion: 'skipped' })) }];
    await f.publish();
    assert.equal(f.created.length, 1);
    assert.deepEqual(f.updated, []);
    assert.match(f.warnings[0], /could not be confirmed/);
});

test('permission denial warns without stronger credentials or altering validation', async () => {
    const f = fixture();
    f.error = { status: 403 };
    await f.publish();
    assert.deepEqual([f.created, f.updated], [[], []]);
    assert.match(f.warnings[0], /token cannot write/);
    assert.equal(f.run.conclusion, 'failure');
});

test('unexpected API failure propagates visibly', async () => {
    const f = fixture();
    f.error = { status: 500 };
    await assert.rejects(f.publish(), error => error.status === 500);
    assert.equal(f.run.conclusion, 'failure');
});

test('failed compiler installation writes summary without querying an installed compiler', async () => {
    const f = fixture();
    await report.summarize({
        core: f.core, context: f.context, configuredVersion: 'v0.89.17',
        steps: { compiler: { outcome: 'failure' }, compile: { outcome: 'skipped' } },
    });
    assert.equal(f.summaries.length, 1);
    assert.match(f.summaries[0], /Observed compiler: unavailable; installation did not succeed/);
    assert.match(f.summaries[0], /Not run: Compile agentic workflows/);
});

for (const [name, command, expected] of [
    ['version', 'printf "gh aw version v0.89.17 (commit abc)\\n"', /Observed compiler: v0\.89\.17/],
    ['invalid-version', 'printf "unrecognized output\\n"', /version output not recognized/],
    ['failed-version', 'exit 1', /version query failed/],
]) {
    test(`summary handles ${name} without losing the primary failure`, () => {
        const cwd = temporaryDirectory(name);
        writeFileSync(join(cwd, 'gh'), `#!/bin/sh\n${command}\n`, { mode: 0o755 });
        // Isolate PATH in a child, rather than mutating shared process state.
        const code = `
            const report = require(${JSON.stringify(require.resolve('../../../.github/workflows/agentic-validation-report.js'))});
            const summaries = [], warnings = [];
            const core = { warning: message => warnings.push(message),
                summary: { addRaw(text) { summaries.push(text); return this; }, async write() {} } };
            report.summarize({ core, context: ${JSON.stringify(fixture().context)},
                configuredVersion: 'v0.89.17', steps: { compiler: { outcome: 'success' }, restore: { outcome: 'failure' } } })
                .then(() => process.stdout.write(JSON.stringify({ summaries, warnings })));
        `;
        const result = JSON.parse(execFileSync(process.execPath, ['-e', code], {
            cwd, env: { ...process.env, PATH: cwd }, encoding: 'utf8',
        }));
        assert.match(result.summaries[0], expected);
        assert.match(result.summaries[0], /Restore failed/);
        assert.equal(result.warnings.length, name === 'version' ? 0 : 1);
    });
}

test('evidence escapes markup, redacts tokens, and truncates Unicode by byte and line budgets', () => {
    const source = '<script>@user</script>\nghp_secrettoken\nhttps://user:password@example.test/path\n::error::literal\n';
    const text = report.evidence(source, 1024, 10);
    assert.match(text, /&lt;script&gt;/);
    assert.equal(text.includes('secrettoken'), false);
    assert.equal(text.includes('password'), false);
    assert.match(text, /::error::literal/);
    const long = report.evidence('🙂'.repeat(10000), report.limits.diff, report.limits.lines);
    assert.ok(Buffer.byteLength(long) <= report.limits.diff);
    assert.match(long, /\[truncated\]/);
    const many = report.evidence('row\n'.repeat(1000), report.limits.diff, report.limits.lines);
    assert.equal(many.split('\n').length, report.limits.lines + 1);
});

function repository(name) {
    const cwd = temporaryDirectory(name);
    mkdirSync(join(cwd, '.github/workflows'), { recursive: true });
    mkdirSync(join(cwd, '.github/aw'), { recursive: true });
    const git = (...args) => execFileSync('git', ['--no-pager', ...args], { cwd, stdio: 'pipe' });
    git('init', '-q');
    git('config', 'user.name', 'Test');
    git('config', 'user.email', 'test@example.com');
    git('config', 'commit.gpgsign', 'false');
    writeFileSync(join(cwd, '.github/workflows/changed.lock.yml'), 'old\n');
    writeFileSync(join(cwd, '.github/workflows/deleted.lock.yml'), 'deleted\n');
    writeFileSync(join(cwd, '.github/aw/actions-lock.json'), '{}\n');
    git('add', '.github');
    git('commit', '-qm', 'Baseline');
    return { cwd, git };
}

function temporaryDirectory(name) {
    const path = mkdtempSync(join(tmpdir(), `aspire-agentic-validation-${name}-`));
    temporaryDirectories.push(path);
    return path;
}

test('generated evidence includes modifications, deletions and untracked files, not unrelated files', async () => {
    const { cwd } = repository('drift');
    writeFileSync(join(cwd, '.github/workflows/changed.lock.yml'), 'new\n');
    unlinkSync(join(cwd, '.github/workflows/deleted.lock.yml'));
    writeFileSync(join(cwd, '.github/workflows/new.lock.yml'), 'untracked-content\n');
    writeFileSync(join(cwd, 'unrelated.txt'), 'UNRELATED-CONTENT\n');
    const text = await report.driftEvidence(cwd);
    assert.match(text, / M .github\/workflows\/changed.lock.yml/);
    assert.match(text, / D .github\/workflows\/deleted.lock.yml/);
    assert.match(text, /\?\? .github\/workflows\/new.lock.yml/);
    assert.match(text, /\+untracked-content/);
    assert.equal(text.includes('UNRELATED-CONTENT'), false);
});

test('untracked symlink evidence does not read its target', async () => {
    const { cwd } = repository('symlink');
    writeFileSync(join(cwd, 'secret.txt'), 'DO-NOT-PUBLISH\n');
    symlinkSync('../../secret.txt', join(cwd, '.github/workflows/link.lock.yml'));
    const text = await report.driftEvidence(cwd);
    assert.equal(text.includes('DO-NOT-PUBLISH'), false);
    assert.match(text, /120000/);
});

test('staged generated drift is included in evidence', async () => {
    const { cwd, git } = repository('staged');
    writeFileSync(join(cwd, '.github/workflows/changed.lock.yml'), 'staged-content\n');
    git('add', '.github/workflows/changed.lock.yml');
    const text = await report.driftEvidence(cwd);
    assert.match(text, /M  .github\/workflows\/changed.lock.yml/);
    assert.match(text, /\+staged-content/);
});

test('large generated changes are bounded and explicitly truncated', async () => {
    const { cwd, git } = repository('large');
    for (let i = 0; i < 25; i++) writeFileSync(join(cwd, `.github/workflows/tracked-${i.toString().padStart(2, '0')}.lock.yml`), Buffer.from([0, i + 1]));
    git('add', '.github/workflows');
    git('commit', '-qm', 'Add generated files');
    for (let i = 0; i < 25; i++) writeFileSync(join(cwd, `.github/workflows/tracked-${i.toString().padStart(2, '0')}.lock.yml`), Buffer.from([0, i + 2]));
    const text = await report.driftEvidence(cwd);
    assert.ok(Buffer.byteLength(text) < report.limits.summary);
    assert.match(text, /5 additional paths omitted/);
    assert.match(text, /truncated/);
    const lineLimit = report.limits.lines;
    try {
        report.limits.lines = 1000;
        const expanded = await report.driftEvidence(cwd);
        assert.match(expanded, /diff --git .*tracked-19\.lock\.yml/);
        assert.doesNotMatch(expanded, /diff --git .*tracked-20\.lock\.yml/);
    } finally {
        report.limits.lines = lineLimit;
    }
});

test('drift summary retains its primary diagnosis if evidence collection fails', async () => {
    const f = fixture();
    const cwd = temporaryDirectory('not-a-repository');
    await assert.rejects(report.summarize({
        core: f.core, context: f.context, configuredVersion: 'v0.89.17', cwd,
        steps: { compiler: { outcome: 'skipped' }, drift: { outcome: 'failure' } },
    }), /git diagnostic collection failed/);
    assert.equal(f.summaries.length, 1);
    assert.match(f.summaries[0], /Verify generated files are up to date failed/);
});
