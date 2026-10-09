// Drives .github/workflows/auto-sec/auto-sec.js for AutoSecWorkflowTests.
//
// argv[2]: request JSON path. argv[3]: result JSON path.
// Request modes:
//   { mode: "call", fn, args }                    -> { value }
//   { mode: "lookup", ecosystem, name, version, now, responses, nugetConfigText } -> { value, urls }
//   { mode: "approve", now, staged, agentItems, pr, prOverrides, files, contents, alerts,
//     malwareNumbers, checkRuns, statuses, reviews, responses, liveHeadSha, liveBaseRef, liveDraft,
//     liveCheckRuns, liveStatuses } -> { value, reviews, summary, info, warnings }
//   { mode: "push-gate", agentItems, pr, prOverrides } -> { value, info, failures }
//   { mode: "patch-gate", patchFiles, workspaceFiles, branchFiles, responses, now } -> { value, info, failures, urls }
//   { mode: "public-text-gate", agentItems, patchFiles } -> { value, info, failures }
//   { mode: "agent-scrub", outputLines, patchFiles, workFiles, ioFailure } -> { value, info, failures, remaining, outputs, stepOutputs }
//   { mode: "publication-guard", workflow } -> { value, repeated } or { error }
//   { mode: "private-shell", script } -> { stdout, stderr, exitCode }
//   { mode: "collect-prs", prs, alerts, changedFiles, contents, failure } -> { output, calls, failed }
// PRs may carry `head_repo` (defaults to microsoft/aspire) and `base_ref` (defaults to main).
// Alerts may carry `vulnerable_version_range` and `advisory_ranges` (every range the
// advisory lists for the package).
// `responses` maps a URL to `{ status, body }`; unknown URLs return 404.
// `prOverrides` maps a PR number to fields that replace `pr` for that number.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');

const gate = require(path.join(__dirname, '..', '..', '..', '.github', 'workflows', 'auto-sec', 'auto-sec.js'));

function reviveDates(value) {
    if (value && typeof value === 'object' && typeof value.now === 'string') {
        return { ...value, now: new Date(value.now) };
    }
    return value;
}

function createFetch(responses, urls) {
    return async url => {
        urls.push(url);
        const response = responses?.[url];
        const status = response?.status ?? 404;
        return {
            status,
            ok: status >= 200 && status < 300,
            json: async () => response?.body ?? null,
        };
    };
}

function createGitHub(request, created) {
    const notFound = () => Object.assign(new Error('Not Found'), { status: 404 });
    const fail = phase => {
        if (request.approvalFailure === phase) {
            throw new Error('synthetic private advisory in API failure');
        }
    };
    const getCalls = new Map();
    let checkCalls = 0;
    let statusCalls = 0;
    const pages = {
        files: () => request.files.map(filename => ({ filename, status: 'modified' })),
        commits: () => (request.commits ?? [{ author_login: 'dependabot[bot]', verified: true }])
            .map(commit => ({ author: { login: commit.author_login }, commit: { verification: { verified: commit.verified } } })),
        // `liveCheckRuns` / `liveStatuses` simulate CI changing after the gates read it.
        checks: () => {
            checkCalls++;
            if (checkCalls > 1) {
                fail('live-checks');
            }
            return checkCalls > 1 && request.liveCheckRuns ? request.liveCheckRuns : (request.checkRuns ?? []);
        },
        reviews: () => (request.reviews ?? []).map(review => ({ user: { login: review.user_login }, state: review.state, commit_id: review.commit_id })),
    };
    const rest = {
        pulls: {
            get: async ({ pull_number: pullNumber }) => {
                const pr = { ...request.pr, number: pullNumber, ...(request.prOverrides?.[pullNumber] ?? {}) };
                // `liveHeadSha` / `liveBaseRef` / `liveDraft` simulate a push, retarget, or draft
                // conversion that lands after the gates were evaluated.
                getCalls.set(pullNumber, (getCalls.get(pullNumber) ?? 0) + 1);
                fail(getCalls.get(pullNumber) > 1 ? 'live-pr' : 'evaluation');
                if (request.liveHeadSha && getCalls.get(pullNumber) > 1) {
                    pr.head_sha = request.liveHeadSha;
                }
                if (request.liveBaseRef && getCalls.get(pullNumber) > 1) {
                    pr.base_ref = request.liveBaseRef;
                }
                if (request.liveDraft && getCalls.get(pullNumber) > 1) {
                    pr.draft = true;
                }
                return {
                    data: {
                        number: pr.number,
                        state: pr.state ?? 'open',
                        draft: pr.draft ?? false,
                        user: { login: pr.user_login ?? 'dependabot[bot]' },
                        head: { sha: pr.head_sha, ref: pr.head_ref, repo: { full_name: pr.head_repo ?? 'microsoft/aspire' } },
                        base: { sha: 'base0000000000000000000000000000000000000', ref: pr.base_ref ?? 'main' },
                        title: pr.title,
                        body: pr.body,
                    },
                };
            },
            listFiles: pages.files,
            listCommits: pages.commits,
            listReviews: pages.reviews,
            createReview: async args => {
                fail('submission');
                created.push(args);
                fail('submission-after-accept');
                return { data: { id: 1 } };
            },
        },
        checks: { listForRef: pages.checks },
        repos: {
            getContent: async ({ path: filePath, ref }) => {
                const key = `${filePath}@${ref === request.pr.head_sha ? 'head' : 'base'}`;
                if (!(key in (request.contents ?? {}))) {
                    throw notFound();
                }
                return { data: request.contents[key] };
            },
            getCombinedStatusForRef: async ({ per_page: perPage = 30, page = 1 }) => {
                // Page 1 starts a new CI read; the second read is the live re-check.
                if (page === 1) {
                    statusCalls++;
                }
                if (statusCalls > 1) {
                    fail('live-statuses');
                }
                const all = (statusCalls > 1 && request.liveStatuses) ? request.liveStatuses : (request.statuses ?? []);
                const states = all.map(status => status.state);
                const state = states.some(s => s === 'failure' || s === 'error') ? 'failure'
                    : (states.length === 0 || states.some(s => s !== 'success')) ? 'pending' : 'success';
                return {
                    data: {
                        state: request.combinedState ?? state,
                        total_count: all.length,
                        statuses: all.slice((page - 1) * perPage, page * perPage),
                    },
                };
            },
        },
    };
    const paginate = async (route, params) => {
        if (typeof route === 'function') {
            return route(params);
        }
        if (route === 'GET /repos/{owner}/{repo}/dependabot/alerts') {
            const alerts = request.alerts ?? [];
            if (params.classification === 'malware') {
                const malware = new Set(request.malwareNumbers ?? []);
                return alerts.filter(alert => malware.has(alert.number)).map(alert => ({ number: alert.number }));
            }
            return alerts.map(alert => ({
                number: alert.number,
                dependency: { package: { ecosystem: alert.ecosystem, name: alert.package }, manifest_path: alert.manifest_path },
                security_vulnerability: {
                    vulnerable_version_range: alert.vulnerable_version_range ?? null,
                    first_patched_version: alert.first_patched_version ? { identifier: alert.first_patched_version } : null,
                },
                security_advisory: {
                    vulnerabilities: (alert.advisory_ranges ?? []).map(range => ({
                        package: { ecosystem: alert.ecosystem, name: alert.package },
                        vulnerable_version_range: range,
                    })),
                },
            }));
        }
        throw new Error(`Unexpected paginate route ${route}`);
    };
    return { rest, paginate };
}

async function main() {
    const request = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
    let result;

    switch (request.mode) {
        case 'collect-prs': {
            // Execute the production inline collector, not a duplicate implementation.
            const source = fs.readFileSync(path.join(__dirname, '..', '..', '..', '.github', 'workflows', 'auto-sec.md'), 'utf8');
            const script = /      node -e '\r?\n([\s\S]*?)\r?\n      '/.exec(source)?.[1];
            if (!script) {
                throw new Error('Missing Dependabot collector');
            }
            const calls = [];
            let output = null;
            let failed = false;
            try {
                vm.runInNewContext(script, {
                    process: { env: { REPO: 'microsoft/aspire' } },
                    require: name => {
                        if (name === 'node:fs') {
                            return {
                                readFileSync: filename => JSON.stringify(filename.endsWith('dependabot-prs-raw.json') ? request.prs : request.alerts),
                                writeFileSync: (filename, value) => { output = JSON.parse(value); },
                            };
                        }
                        if (name === 'node:child_process') {
                            return {
                                execFileSync: (command, args) => {
                                    const endpoint = args.at(-1);
                                    calls.push(endpoint);
                                    if (endpoint === request.failure?.endpoint) {
                                        throw Object.assign(new Error('synthetic private API failure'), { status: request.failure.status });
                                    }
                                    if (endpoint.includes('/pulls/')) {
                                        return JSON.stringify(request.changedFiles);
                                    }
                                    if (Object.hasOwn(request.contents, endpoint)) {
                                        return request.contents[endpoint];
                                    }
                                    throw new Error('Unexpected content request');
                                },
                            };
                        }
                        return gate;
                    },
                });
            } catch {
                failed = true;
            }
            result = { output, calls, failed };
            break;
        }
        case 'call': {
            const value = await gate[request.fn](...request.args.map(reviveDates));
            result = { value: value instanceof Set ? [...value].sort() : value };
            break;
        }
        case 'lookup': {
            const urls = [];
            const value = await gate.lookupPackageVersion(request.ecosystem, request.name, request.version, {
                fetchImpl: createFetch(request.responses, urls),
                now: new Date(request.now),
                nugetConfigText: request.nugetConfigText ?? null,
            });
            result = { value, urls };
            break;
        }
        case 'approve': {
            const reviews = [];
            const info = [];
            const warnings = [];
            const failures = [];
            let summary = '';
            const outputDir = fs.mkdtempSync(path.join(os.tmpdir(), 'auto-sec-'));
            const outputPath = path.join(outputDir, 'agent_output.json');
            fs.writeFileSync(outputPath, request.approvalFailure === 'parse-output' ? 'synthetic private advisory invalid JSON' : JSON.stringify({ items: request.agentItems ?? [] }));
            const core = {
                info: message => info.push(message),
                warning: message => warnings.push(message),
                setFailed: message => failures.push(message),
                summary: {
                    addHeading: text => { summary += `${text}\n`; return core.summary; },
                    addRaw: text => { summary += text; return core.summary; },
                    write: async () => {
                        if (request.approvalFailure === 'summary') {
                            throw new Error('synthetic private advisory in summary failure');
                        }
                    },
                },
            };
            try {
                const value = await gate.runApprovalJob({
                    github: createGitHub(request, []),
                    approver: createGitHub(request, reviews),
                    context: { repo: { owner: 'microsoft', repo: 'aspire' } },
                    core,
                    fs: new Proxy(fs, {
                        get: (target, property) => property === 'readFileSync' && request.approvalFailure === 'read-output'
                            ? () => { throw new Error('synthetic private advisory in file failure'); }
                            : target[property],
                    }),
                    env: { GH_AW_AGENT_OUTPUT: outputPath, GH_AW_SAFE_OUTPUTS_STAGED: request.staged ? 'true' : 'false' },
                    fetchImpl: createFetch(request.responses, []),
                    now: new Date(request.now),
                });
                result = { value, reviews, summary, info, warnings, failures };
            } finally {
                fs.rmSync(outputDir, { recursive: true, force: true });
            }
            break;
        }
        case 'push-gate': {
            const info = [];
            const failures = [];
            const outputDir = fs.mkdtempSync(path.join(os.tmpdir(), 'auto-sec-'));
            const outputPath = path.join(outputDir, 'agent_output.json');
            fs.writeFileSync(outputPath, JSON.stringify({ items: request.agentItems ?? [] }));
            try {
                const value = await gate.runPushTargetGate({
                    github: createGitHub(request, []),
                    context: { repo: { owner: 'microsoft', repo: 'aspire' } },
                    core: { info: message => info.push(message), setFailed: message => failures.push(message) },
                    env: { GH_AW_AGENT_OUTPUT: outputPath, AUTO_SEC_BOT_LOGIN: request.botLogin },
                });
                result = { value, info, failures };
            } finally {
                fs.rmSync(outputDir, { recursive: true, force: true });
            }
            break;
        }
        case 'patch-gate': {
            const info = [];
            const failures = [];
            const root = fs.mkdtempSync(path.join(os.tmpdir(), 'auto-sec-'));
            const patchDir = path.join(root, 'patches');
            const workspace = path.join(root, 'workspace');
            for (const [name, text] of Object.entries(request.patchFiles ?? {})) {
                fs.mkdirSync(patchDir, { recursive: true });
                fs.writeFileSync(path.join(patchDir, name), text);
            }
            for (const [name, text] of Object.entries(request.workspaceFiles ?? {})) {
                fs.mkdirSync(path.dirname(path.join(workspace, name)), { recursive: true });
                fs.writeFileSync(path.join(workspace, name), text);
            }
            const branchFiles = request.branchFiles ?? {};
            const github = {
                rest: {
                    repos: {
                        getContent: async ({ path: file, ref }) => {
                            if (ref !== gate.AUTO_SEC_BRANCH || !(file in branchFiles)) {
                                throw Object.assign(new Error('Not Found'), { status: 404 });
                            }
                            return { data: { content: Buffer.from(branchFiles[file], 'utf8').toString('base64') } };
                        },
                    },
                },
            };
            const urls = [];
            try {
                const value = await gate.runPatchContentGate({
                    core: { info: message => info.push(message), setFailed: message => failures.push(message) },
                    github,
                    context: { repo: { owner: 'microsoft', repo: 'aspire' } },
                    patchDir,
                    workspace,
                    fetchImpl: createFetch(request.responses, urls),
                    now: new Date(request.now ?? '2026-01-01T00:00:00Z'),
                });
                result = { value, info, failures, urls };
            } finally {
                fs.rmSync(root, { recursive: true, force: true });
            }
            break;
        }
        case 'public-text-gate': {
            const info = [];
            const failures = [];
            const root = fs.mkdtempSync(path.join(os.tmpdir(), 'auto-sec-'));
            const patchDir = path.join(root, 'patches');
            const outputPath = path.join(root, 'agent_output.json');
            const workspace = path.join(root, 'workspace');
            for (const [name, text] of Object.entries(request.workspaceFiles ?? {})) {
                fs.mkdirSync(path.dirname(path.join(workspace, name)), { recursive: true });
                fs.writeFileSync(path.join(workspace, name), text);
            }
            const github = { rest: { repos: { getContent: async ({ path: file }) => {
                if (!Object.hasOwn(request.branchFiles ?? {}, file)) {
                    throw Object.assign(new Error('Not Found'), { status: 404 });
                }
                return { data: { content: Buffer.from(request.branchFiles[file], 'utf8').toString('base64') } };
            } } } };
            fs.writeFileSync(outputPath, JSON.stringify({ items: request.agentItems ?? [] }));
            for (const [name, text] of Object.entries(request.patchFiles ?? {})) {
                fs.mkdirSync(patchDir, { recursive: true });
                fs.writeFileSync(path.join(patchDir, name), text);
            }
            try {
                const value = await gate.runPublicTextGate({
                    core: { info: message => info.push(message), setFailed: message => failures.push(message) },
                    env: { GH_AW_AGENT_OUTPUT: outputPath, GITHUB_WORKSPACE: workspace },
                    patchDir,
                    github,
                    context: { repo: { owner: 'microsoft', repo: 'aspire' } },
                    readBaseTexts: request.useCheckedOutBases ? null : file => [request.baseFiles?.[file]],
                });
                result = { value, info, failures };
            } finally {
                fs.rmSync(root, { recursive: true, force: true });
            }
            break;
        }
        case 'agent-scrub': {
            const info = [];
            const failures = [];
            const stepOutputs = {};
            const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'auto-sec-'));
            const outputPath = path.join(workDir, request.outputRelativePath ?? 'outputs.jsonl');
            const checkout = request.useGitSnapshot ? fs.mkdtempSync(path.join(os.tmpdir(), 'auto-sec-checkout-')) : null;
            let baseSha = '0123456789abcdef0123456789abcdef01234567';
            fs.mkdirSync(path.dirname(outputPath), { recursive: true });
            fs.writeFileSync(outputPath, (request.outputLines ?? []).join('\n'));
            for (const [name, text] of Object.entries({ ...(request.patchFiles ?? {}), ...(request.workFiles ?? {}) })) {
                fs.mkdirSync(path.dirname(path.join(workDir, name)), { recursive: true });
                fs.writeFileSync(path.join(workDir, name), text);
            }
            try {
                if (checkout) {
                    const exec = require('node:child_process').execFileSync;
                    const options = { cwd: checkout, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] };
                    exec('git', ['init', '--quiet'], options);
                    const emptyConfig = path.join(checkout, '.git', 'fixture-empty-config');
                    fs.writeFileSync(emptyConfig, '');
                    options.env = { ...process.env, GIT_CONFIG_GLOBAL: emptyConfig, GIT_CONFIG_NOSYSTEM: '1' };
                    if (!request.missingGitSnapshot) {
                        for (const [file, text] of Object.entries(request.baseFiles ?? {})) {
                            fs.mkdirSync(path.dirname(path.join(checkout, file)), { recursive: true });
                            fs.writeFileSync(path.join(checkout, file), text);
                        }
                        exec('git', ['-c', 'core.autocrlf=false', 'add', '--all'], options);
                        baseSha = exec('git', ['write-tree'], options).toString().trim();
                        for (const text of request.extraBlobs ?? []) {
                            exec('git', ['hash-object', '-w', '--stdin'], { ...options, input: text });
                        }
                        if (request.generatedPatch) {
                            const commitOptions = { ...options, env: { ...options.env,
                                GIT_AUTHOR_NAME: 'github-actions[bot]', GIT_COMMITTER_NAME: 'github-actions[bot]',
                                GIT_AUTHOR_EMAIL: 'github-actions[bot]@users.noreply.github.com', GIT_COMMITTER_EMAIL: 'github-actions[bot]@users.noreply.github.com',
                                GIT_AUTHOR_DATE: '2026-10-03T09:00:00+00:00', GIT_COMMITTER_DATE: '2026-10-03T09:00:00+00:00',
                            } };
                            baseSha = exec('git', ['commit-tree', baseSha, '-m', 'Fixture base'], commitOptions).toString().trim();
                            let head = baseSha;
                            for (const commit of request.generatedPatch.commits ?? [request.generatedPatch]) {
                                for (const [file, text] of Object.entries(commit.headFiles)) {
                                    fs.mkdirSync(path.dirname(path.join(checkout, file)), { recursive: true });
                                    fs.writeFileSync(path.join(checkout, file), text);
                                }
                                exec('git', ['-c', 'core.autocrlf=false', 'add', '--all'], options);
                                const headTree = exec('git', ['write-tree'], options).toString().trim();
                                head = exec('git', ['commit-tree', headTree, '-p', head, '-F', '-'],
                                    { ...commitOptions, input: commit.message }).toString().trim();
                            }
                            const format = ['format-patch', '--stdout', `${baseSha}..${head}`, ...(request.generatedPatch.fullIndex ? ['--full-index'] : [])];
                            let patch = exec('git', format, commitOptions).toString();
                            if (request.generatedPatch.embedBaseCommit) {
                                patch = patch.replace(/^(From [^\n]*\n)/, `$1X-GH-AW-Base-Commit: ${baseSha}\n`);
                            }
                            const name = request.generatedPatch.repoScoped ? 'aw-microsoft-aspire-auto-sec-security-updates.patch' : 'aw-auto-sec-security-updates.patch';
                            fs.writeFileSync(path.join(workDir, name), patch);
                        }
                    }
                }
                const scrubFs = new Proxy(fs, {
                    get(target, property) {
                        if (property !== request.ioFailure?.operation) {
                            return target[property];
                        }
                        return (filename, ...args) => {
                            if (path.relative(workDir, filename).replace(/\\/g, '/') === request.ioFailure.path) {
                                throw Object.assign(new Error('EACCES synthetic private GHSA-xxxx content'), { code: 'EACCES' });
                            }
                            return target[property](filename, ...args);
                        };
                    },
                });
                const value = await gate.runAgentOutputScrub({
                    core: {
                        info: message => info.push(message),
                        setFailed: message => failures.push(message),
                        setOutput: (name, value) => { stepOutputs[name] = value; },
                    },
                    fs: scrubFs,
                    github: { rest: { repos: {
                        getBranch: async ({ branch }) => {
                            if (branch !== 'auto-sec/security-updates') {
                                throw new Error('Unexpected branch');
                            }
                            if (!request.branchSha) {
                                throw Object.assign(new Error('Not Found'), { status: 404 });
                            }
                            return { data: { commit: { sha: request.branchSha } } };
                        },
                        getContent: async ({ path: file, ref }) => {
                            if (ref !== 'auto-sec/security-updates') {
                                throw new Error('Unexpected branch');
                            }
                            if (!Object.hasOwn(request.branchFiles ?? {}, file)) {
                                throw Object.assign(new Error('Not Found'), { status: 404 });
                            }
                            return { data: { content: Buffer.from(request.branchFiles[file], 'utf8').toString('base64') } };
                        },
                    } } },
                    context: { repo: { owner: 'microsoft', repo: 'aspire' } },
                    env: { GH_AW_SAFE_OUTPUTS: outputPath, GITHUB_WORKSPACE: checkout ?? undefined, GITHUB_SHA: baseSha },
                    workDir,
                    readBaseTexts: request.useGitSnapshot ? null : file => [request.baseFiles?.[file]],
                });
                const remaining = fs.readdirSync(workDir, { recursive: true })
                    .map(name => String(name).replace(/\\/g, '/'))
                    .filter(name => name !== path.relative(workDir, outputPath).replace(/\\/g, '/') && fs.statSync(path.join(workDir, name)).isFile())
                    .sort();
                const outputs = fs.readFileSync(outputPath, 'utf8').split('\n').filter(line => line !== '');
                // Evaluate the actual generated conditions with Actions' always() behavior,
                // so a failed scrub would expose the original unconditional publishers.
                // Expressions come only from the trusted workflow fixture, never agent data.
                const publications = (request.publicationConditions ?? []).filter(condition => vm.runInNewContext(condition, {
                    steps: { auto_sec_scrub: { outcome: failures.length > 0 ? 'failure' : 'success', outputs: stepOutputs } },
                    always: () => true,
                    success: () => failures.length === 0,
                }, { timeout: 1000 }));
                result = { value, info, failures, remaining, outputs, stepOutputs, publications };
            } finally {
                fs.rmSync(workDir, { recursive: true, force: true });
                if (checkout) {
                    fs.rmSync(checkout, { recursive: true, force: true });
                }
            }
            break;
        }
        case 'publication-guard': {
            const { guardPublications } = require(path.join(__dirname, '..', '..', '..', '.github', 'workflows', 'auto-sec', 'publication-guard.js'));
            try {
                const value = guardPublications(request.workflow);
                result = { value, repeated: guardPublications(value) };
            } catch (error) {
                result = { error: error.message };
            }
            break;
        }
        case 'private-shell': {
            const { spawnSync } = require('node:child_process');
            const child = spawnSync('bash', ['-e', '-s'], {
                input: request.script,
                encoding: 'utf8',
                windowsHide: true,
                timeout: 30000,
            });
            if (child.error) {
                throw child.error;
            }
            result = { stdout: child.stdout, stderr: child.stderr, exitCode: child.status };
            break;
        }
        case 'publication-decisions': {
            const publications = request.conditions.filter(condition => vm.runInNewContext(condition, {
                steps: { auto_sec_scrub: { outcome: request.outcome, outputs: request.stepOutputs } },
                always: () => true,
                success: () => request.outcome === 'success',
            }, { timeout: 1000 }));
            result = { publications };
            break;
        }
        default:
            throw new Error(`Unknown mode ${request.mode}`);
    }

    fs.writeFileSync(process.argv[3], JSON.stringify(result));
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
