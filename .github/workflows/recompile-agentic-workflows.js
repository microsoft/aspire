'use strict';

const fs = require('node:fs');
const { execFileSync } = require('node:child_process');

const compilerFiles = [
    '.github/workflows/copilot-setup-steps.yml',
    '.github/workflows/validate-agentic-workflows.yml',
];
const releasePattern = /^v\d+\.\d+\.\d+$/;

// Dependabot pins look like:
//   +        uses: github/gh-aw-actions/setup@<40-character SHA> # v0.89.17
// A missing release comment is resolved through upstream tags, never the PR title.
function actionReferences(text) {
    return [...text.matchAll(/^\s*(?:-\s*)?uses:\s*github\/gh-aw-actions\/[\w/-]+@([a-f0-9]{40}|v\d+\.\d+\.\d+)(?:\s+#\s*(v\d+\.\d+\.\d+)\b)?\s*$/gm)]
        .map(match => ({ ref: match[1], version: match[2] }));
}

async function resolveVersion(reference, github) {
    const version = reference.version ?? (releasePattern.test(reference.ref) ? reference.ref : undefined);
    if (version) {
        const { data } = await github.rest.repos.getCommit({
            owner: 'github', repo: 'gh-aw-actions', ref: version,
        });
        if (reference.ref !== version && data.sha !== reference.ref) {
            throw new Error(`gh-aw action pin ${reference.ref} does not match ${version}.`);
        }
        return version;
    }

    const tags = await github.paginate(github.rest.repos.listTags, {
        owner: 'github', repo: 'gh-aw-actions', per_page: 100,
    });
    const versions = tags.filter(tag => tag.commit.sha === reference.ref && releasePattern.test(tag.name));
    if (versions.length !== 1) {
        throw new Error(`Cannot uniquely map gh-aw action pin ${reference.ref} to a release.`);
    }
    return versions[0].name;
}

function synchronizeCompiler(content, version, sha) {
    let count = 0;
    const result = content.replace(
        /(^[ \t]*uses: github\/gh-aw-actions\/setup-cli@)[^\r\n]+(\r?\n[ \t]*with:\r?\n[ \t]*version: )[^\r\n]+/gm,
        (_, prefix, input) => {
            count++;
            return `${prefix}${sha} # ${version}${input}${version}`;
        });
    if (count !== 1) {
        throw new Error('Expected exactly one setup-cli action with a version input.');
    }
    return result;
}

async function detect({ github, context, core }, io = fs) {
    if (context.eventName === 'workflow_dispatch') {
        if (context.repo.owner !== 'microsoft' || context.repo.repo !== 'aspire' ||
            context.ref !== `refs/heads/${context.payload.repository.default_branch}`) {
            throw new Error('Manual recompilation requires the upstream default branch.');
        }
        const references = new Map();
        for (const file of io.readdirSync('.github/workflows', { recursive: true })) {
            if (!/\.ya?ml$/.test(file)) {
                continue;
            }
            for (const reference of actionReferences(io.readFileSync(`.github/workflows/${file}`, 'utf8'))) {
                references.set(`${reference.ref}:${reference.version}`, reference);
            }
        }
        const versions = [];
        for (const reference of references.values()) {
            versions.push(await resolveVersion(reference, github));
        }
        if (versions.length === 0) {
            throw new Error('No pinned gh-aw action releases found on the default branch.');
        }
        const version = versions.sort(compareVersions).at(-1);
        await prepareCompilation({
            github, core, version, branch: 'recompile-agentic-workflows-manual',
            source: 'the default branch action pins (manual dispatch)',
        }, io);
        return;
    }
    const pr = context.payload.pull_request;
    if (context.eventName !== 'pull_request' || context.payload.action !== 'closed' ||
        context.repo.owner !== 'microsoft' || context.repo.repo !== 'aspire' ||
        !pr?.merged || pr.user.login !== 'dependabot[bot]' ||
        pr.head.repo.full_name !== 'microsoft/aspire' ||
        pr.base.ref !== context.payload.repository.default_branch) {
        throw new Error('Recompilation requires a merged upstream Dependabot PR targeting the default branch.');
    }

    const branch = `recompile-agentic-workflows-${pr.number}`;
    const existing = await github.paginate(github.rest.pulls.list, {
        ...context.repo, head: `${context.repo.owner}:${branch}`, state: 'all', per_page: 100,
    });
    if (existing.some(pull => pull.state === 'closed')) {
        core.notice('The recompilation PR is already closed; not reopening it on a rerun.');
        return;
    }

    const files = await github.paginate(github.rest.pulls.listFiles, {
        ...context.repo, pull_number: pr.number, per_page: 100,
    });
    const candidates = [];
    for (const file of files.filter(file => /^\.github\/workflows\/.*\.ya?ml$/.test(file.filename) && file.status !== 'removed')) {
        if (!file.patch) {
            throw new Error(`Missing PR diff for ${file.filename}; cannot safely detect a gh-aw update.`);
        }
        const added = actionReferences(file.patch.split('\n').filter(line => line.startsWith('+')).map(line => line.slice(1)).join('\n'));
        const removed = actionReferences(file.patch.split('\n').filter(line => line.startsWith('-')).map(line => line.slice(1)).join('\n'));
        for (const reference of added.filter(reference => !removed.some(old => old.ref === reference.ref))) {
            const current = actionReferences(io.readFileSync(file.filename, 'utf8'));
            if (!current.some(item => item.ref === reference.ref)) {
                core.notice('The merged gh-aw update has been superseded on the default branch.');
                return;
            }
            if (!candidates.some(item => item.ref === reference.ref && item.version === reference.version)) {
                candidates.push(reference);
            }
        }
    }
    if (candidates.length === 0) {
        core.info('The merged PR did not change any gh-aw action pins.');
        return;
    }
    const versions = new Set();
    for (const reference of candidates) {
        versions.add(await resolveVersion(reference, github));
    }
    if (versions.size !== 1) {
        throw new Error('The merged PR contains conflicting gh-aw releases.');
    }
    const [version] = versions;
    await prepareCompilation({ github, core, version, branch, source: `Dependabot PR #${pr.number}` }, io);
}

function compareVersions(left, right) {
    const rightParts = right.slice(1).split('.').map(Number);
    return left.slice(1).split('.').map(Number)
        .map((part, index) => part - rightParts[index]).find(part => part !== 0) ?? 0;
}

async function prepareCompilation({ github, core, version, branch, source }, io) {
    const compilerContents = compilerFiles.map(file => [file, io.readFileSync(file, 'utf8')]);
    for (const [, content] of compilerContents) {
        for (const reference of actionReferences(content)) {
            const currentVersion = reference.version ?? reference.ref;
            if (!releasePattern.test(currentVersion)) {
                throw new Error('The current compiler pin must identify its release.');
            }
            if (compareVersions(currentVersion, version) > 0) {
                core.notice('A newer compiler is already pinned on the default branch; not downgrading it.');
                return;
            }
        }
    }
    const { data } = await github.rest.repos.getCommit({
        owner: 'github', repo: 'gh-aw-actions', ref: version,
    });
    // Compute every edit first so an unexpected compiler-pin shape cannot leave
    // a partially synchronized checkout.
    const updates = compilerContents.map(([file, content]) => [file, synchronizeCompiler(content, version, data.sha)]);
    for (const [file, content] of updates) {
        io.writeFileSync(file, content);
    }
    core.setOutput('version', version);
    core.setOutput('branch', branch);
    core.setOutput('source', source);
    core.setOutput('updated', 'true');
}

function allowedChange(file) {
    return compilerFiles.includes(file) || file === '.github/aw/actions-lock.json' ||
        /^\.github\/workflows\/(?:[^/]+\/)*[^/]+\.lock\.yml$/.test(file) ||
        /^\.github\/workflows\/agentics-maintenance[^/]*\.yml$/.test(file);
}

function checkChanges(run = execFileSync, io = fs) {
    const paths = [
        ...run('git', ['diff', '--name-only', '-z', 'HEAD'], { encoding: 'utf8' }).split('\0'),
        ...run('git', ['ls-files', '--others', '--exclude-standard', '-z'], { encoding: 'utf8' }).split('\0'),
    ].filter(Boolean);
    const unexpected = paths.filter(file => !allowedChange(file));
    if (unexpected.length) {
        throw new Error(`Compilation changed unexpected files: ${unexpected.join(', ')}`);
    }
    io.appendFileSync(process.env.GITHUB_OUTPUT, `changed=${paths.length > 0}\n`);
}

module.exports = { actionReferences, resolveVersion, synchronizeCompiler, detect, allowedChange, checkChanges };

if (require.main === module) {
    if (process.argv[2] !== 'changes') {
        throw new Error('Expected the changes command.');
    }
    checkChanges();
}
