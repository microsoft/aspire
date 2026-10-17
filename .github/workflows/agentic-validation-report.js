// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

const { spawn } = require('node:child_process');

const workflowPath = '.github/workflows/validate-agentic-workflows.yml';
const marker = '<!-- agentic-validation-report -->';
const stages = [
    ['checkout', 'Checkout validation sources', 'Check the checkout error in the linked step before retrying.'],
    ['compiler', 'Install gh-aw extension', 'Check the extension download/install error. Compilation has not run; regenerating files is not yet indicated.'],
    ['compile', 'Compile agentic workflows (schema and action-pin validation)', 'Fix the compiler diagnostics in the workflow Markdown or action-pin configuration, then compile with the CI-pinned gh-aw version.'],
    ['drift', 'Verify generated files are up to date', 'Regenerate with the CI-pinned gh-aw version, review the diff, and commit the generated files with their sources. Do not edit generated workflows manually.'],
    ['lint', 'Lint generated agentic workflows', 'Run `gh aw lint --shellcheck`, fix the reported source diagnostics, and regenerate affected workflows.'],
    ['sdk', 'Set up .NET SDK', 'Check the SDK setup error in the linked step. Workflow contract tests have not run.'],
    ['restore', 'Restore', 'Inspect the restore error in the linked step. Workflow contract tests have not run; regeneration alone will not address a restore failure.'],
    ['contracts', 'Run Infrastructure.Tests agentic workflow contracts', 'Inspect the failing contract or build diagnostic in the linked step and run the `Category=AgenticWorkflow` tests locally.'],
];
const generatedPaths = [
    '.github/aw/actions-lock.json',
    '.github/workflows/*.lock.yml',
    '.github/workflows/agentics-maintenance*.yml',
];
const limits = { summary: 24 * 1024, diff: 12 * 1024, lines: 100, paths: 20, comment: 4 * 1024 };
const versionPattern = /^v\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$/;

function escape(value) {
    return String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
        .replaceAll('\r', '').replaceAll('\0', '');
}

function truncate(value, bytes) {
    const buffer = Buffer.from(value);
    return buffer.length > bytes
        ? buffer.subarray(0, bytes).toString('utf8').replace(/\uFFFD$/, '') + '\n[truncated]'
        : value;
}

function evidence(value, bytes, lines) {
    // Do not echo raw diagnostics to stdout: embedded "::error::" text would be
    // interpreted as a runner command. Render escaped text only in the summary.
    const redacted = value
        .replace(/(?:github_pat_|gh[pousr]_)[a-zA-Z0-9_]+/g, '[REDACTED]')
        .replace(/(https?:\/\/)[^/\s:@]+:[^/\s@]+@/g, '$1[REDACTED]@');
    const rows = redacted.split('\n');
    const excerpt = rows.slice(0, lines).join('\n') + (rows.length > lines ? '\n[truncated]' : '');
    return `<pre>${truncate(escape(excerpt), bytes - 32)}</pre>`;
}

function sourceUrl(context, runId, attempt) {
    return `${context.serverUrl}/${context.repo.owner}/${context.repo.repo}/actions/runs/${runId}/attempts/${attempt}`;
}

function matchStage(name) {
    return stages.find(([, title]) => name === title ||
        (title === 'Install gh-aw extension' && /^Install gh-aw extension \(v\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?\)$/.test(name)));
}

// Drain the process even after reaching the capture limit, without buffering a
// potentially huge generated diff or logging PR-controlled content.
function capture(command, args, { cwd, maxBytes = 64 * 1024, allowedCodes = [0] } = {}) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { cwd, stdio: ['ignore', 'pipe', 'pipe'] });
        const chunks = [];
        let length = 0;
        let truncated = false;
        child.stdout.on('data', data => {
            const remaining = maxBytes - length;
            if (data.length > remaining) truncated = true;
            const chunk = data.subarray(0, remaining);
            chunks.push(chunk);
            length += chunk.length;
        });
        child.stderr.resume();
        child.on('error', reject);
        child.on('close', code => {
            if (!allowedCodes.includes(code)) {
                const error = new Error(`${command} diagnostic collection failed (exit ${code}); see the original validation step.`);
                error.exitCode = code;
                reject(error);
            } else {
                resolve({ text: Buffer.concat(chunks).toString('utf8'), truncated });
            }
        });
    });
}

async function driftEvidence(cwd) {
    const status = await capture('git', ['--no-pager', 'status', '--porcelain=v1', '-z', '--no-renames',
        '--untracked-files=all', '--', ...generatedPaths], { cwd });
    // --porcelain=v1 -z produces "XY path\0", including literal newlines in paths.
    // Disabling renames avoids the second NUL-delimited path of rename records.
    const records = status.text.split('\0').slice(0, -1);
    const selectedRecords = records.slice(0, limits.paths);
    const omitted = records.length - selectedRecords.length;
    let text = `### Generated-file evidence\n\n${evidence(selectedRecords.join('\n'), 4 * 1024, 60)}\n`;
    if (omitted || status.truncated) text += `\n${status.truncated ? 'At least ' : ''}${omitted} additional paths omitted; evidence truncated.\n`;

    const trackedPaths = selectedRecords.filter(record => !record.startsWith('?? ')).map(record => record.slice(3));
    const diff = trackedPaths.length === 0
        ? { text: '', truncated: false }
        : await capture('git', ['--literal-pathspecs', '--no-pager', 'diff', '--no-ext-diff', '--no-textconv',
            '--no-color', 'HEAD', '--', ...trackedPaths], { cwd, maxBytes: limits.diff });
    let combined = diff.text;
    let clipped = diff.truncated;
    // git diff does not show untracked locks. --no-index displays their content
    // (or symlink target) without following a link outside the workspace.
    for (const record of selectedRecords.filter(record => record.startsWith('?? '))) {
        if (Buffer.byteLength(combined) >= limits.diff) {
            clipped = true;
            break;
        }
        const added = await capture('git', ['--no-pager', 'diff', '--no-index', '--no-ext-diff',
            '--no-textconv', '--no-color', '--', '/dev/null', record.slice(3)],
        { cwd, maxBytes: limits.diff - Buffer.byteLength(combined), allowedCodes: [0, 1] });
        combined += added.text;
        clipped ||= added.truncated;
    }
    text += `\n${evidence(combined, limits.diff, limits.lines)}\n`;
    if (clipped) text += '\nDiff truncated; inspect the generated files locally for the full changes.\n';
    return text;
}

async function summarize({ core, context, steps, configuredVersion, cwd }) {
    const failed = stages.filter(([id]) => steps[id]?.outcome === 'failure');
    const skipped = stages.filter(([id]) => steps[id]?.outcome === 'skipped');
    const runUrl = sourceUrl(context, context.runId, process.env.GITHUB_RUN_ATTEMPT || 1);
    let body = `## Agentic workflow validation failed\n\n[Validation run](${runUrl})\n\n`;
    let writtenBytes = 0;
    body += `Configured compiler: ${versionPattern.test(configuredVersion) ? configuredVersion : 'unavailable'}.\n\n`;
    if (steps.compiler?.outcome === 'success') {
        try {
            const observed = await capture('gh', ['aw', 'version'], { cwd, maxBytes: 1024 });
            // gh-aw prints "gh aw version v0.89.17 (...)"; only publish the version,
            // never arbitrary command output.
            const version = observed.text.match(/\bv\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?\b/)?.[0];
            body += `Observed compiler: ${version || 'unavailable (version output not recognized)'}.\n\n`;
            if (!version) core.warning('The compiler version output was not recognized.');
        } catch (error) {
            if (error.code !== 'ENOENT' && !Object.hasOwn(error, 'exitCode')) throw error;
            core.warning('Unable to query the installed compiler version; inspect the installation step.');
            body += 'Observed compiler: unavailable; the version query failed.\n\n';
        }
    } else {
        body += 'Observed compiler: unavailable; installation did not succeed.\n\n';
    }
    for (const [, title, advice] of failed) body += `**${title} failed.** ${advice}\n\n`;
    if (!failed.length) body += 'Failure was outside the recognized validation stages. Inspect the run logs.\n\n';
    if (skipped.length) body += `Not run: ${skipped.map(([, title]) => title).join('; ')}.\n\n`;
    if (steps.drift?.outcome === 'failure') {
        body += '```shell\ngh aw compile --purge --force-refresh-action-pins --validate --no-check-update\n```\n\n';
        body += 'CI validates the PR merge result. If the branch alone is clean, reproduce with the current base incorporated. '
            + 'Check compiler/source/action-pin alignment; drift alone does not establish which change caused it.\n\n';
        // Publish the primary diagnosis before collecting optional Git evidence.
        // A collection failure then leaves a useful summary and fails visibly.
        writtenBytes = Buffer.byteLength(body);
        if (writtenBytes > limits.summary) throw new Error('Validation summary exceeded its size budget.');
        await core.summary.addRaw(body).write();
        body = await driftEvidence(cwd);
    }
    if (writtenBytes + Buffer.byteLength(body) > limits.summary) throw new Error('Validation summary exceeded its size budget.');
    await core.summary.addRaw(body).write();
}

function sameHead(pr, run, repositoryId) {
    return pr.state === 'open' && pr.base?.repo?.id === repositoryId &&
        pr.head?.repo?.id === run.head_repository?.id && pr.head?.ref === run.head_branch &&
        pr.head?.sha === run.head_sha;
}

async function publish({ github, context, core }) {
    const { owner, repo } = context.repo;
    const event = context.payload.workflow_run;
    const repositoryId = context.payload.repository.id;
    const runUrl = sourceUrl(context, event.id, event.run_attempt);
    async function skip(reason) {
        core.warning(reason);
        await core.summary.addRaw(`Agentic validation comment not published: ${reason}\n\n[Validation run](${runUrl})`).write();
    }
    const { data: run } = await github.rest.actions.getWorkflowRun({ owner, repo, run_id: event.id });
    if (run.repository?.id !== repositoryId || run.path !== workflowPath || run.event !== 'pull_request' ||
        run.status !== 'completed' || !['failure', 'success'].includes(run.conclusion) ||
        run.run_attempt !== event.run_attempt || run.workflow_id !== event.workflow_id) {
        return skip('The source is not the expected completed validation run/attempt.');
    }
    const { data: workflow } = await github.rest.actions.getWorkflow({ owner, repo, workflow_id: run.workflow_id });
    if (workflow.path !== workflowPath) return skip('The source workflow identity does not match.');
    if (!run.head_repository?.id || !run.head_repository.owner?.login || !run.head_branch ||
        !/^[a-f0-9]{40}$/.test(run.head_sha)) {
        return skip('The source head repository, branch, or commit is unavailable.');
    }

    const associated = run.pull_requests || [];
    let candidates;
    if (associated.length) {
        candidates = await Promise.all(associated.map(async pr =>
            (await github.rest.pulls.get({ owner, repo, pull_number: pr.number })).data));
    } else {
        // Fork run payloads can omit pull_requests. Never accept an artifact's PR
        // number: find one exact live repository/branch/SHA match through the API.
        candidates = await github.paginate(github.rest.pulls.list, {
            owner, repo, state: 'open', head: `${run.head_repository.owner.login}:${run.head_branch}`, per_page: 100,
        });
    }
    const matching = candidates.filter(pr => sameHead(pr, run, repositoryId));
    if (matching.length !== 1) return skip('No unique current open PR is associated with this validation.');
    const issue_number = matching[0].number;

    async function current() {
        const { data: live } = await github.rest.pulls.get({ owner, repo, pull_number: issue_number });
        if (!sameHead(live, run, repositoryId)) return false;
        const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
            owner, repo, workflow_id: run.workflow_id, event: 'pull_request', head_sha: run.head_sha, per_page: 100,
        });
        return runs.some(candidate => candidate.id === run.id && candidate.run_attempt === run.run_attempt) &&
            !runs.some(candidate => candidate.head_repository?.id === run.head_repository.id &&
                candidate.head_branch === run.head_branch &&
                (candidate.run_number > run.run_number ||
                    (candidate.id === run.id && candidate.run_attempt > run.run_attempt)));
    }
    if (!await current()) return skip('A newer run, attempt, or PR head supersedes this result.');

    const jobs = await github.paginate('GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs',
        { owner, repo, run_id: run.id, attempt_number: run.run_attempt, per_page: 100 });
    const steps = jobs.flatMap(job => job.steps || []);
    if (run.conclusion === 'success' &&
        !stages.every(([, title]) => steps.some(step => matchStage(step.name)?.[1] === title && step.conclusion === 'success'))) {
        return skip('Successful completion of every validation stage could not be confirmed; the failure comment is not resolved.');
    }
    const compiler = steps.map(step => step.name.match(/^Install gh-aw extension \((v\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?)\)$/)?.[1]).find(Boolean);
    const failedStages = [...new Set(steps.filter(step => step.conclusion === 'failure')
        .map(step => matchStage(step.name)).filter(Boolean))];
    let body = `[automated] ${marker}\n\n`;
    if (run.conclusion === 'success') {
        body += '**Agentic workflow validation is resolved.** The current validation passed.\n\n';
    } else {
        body += '**Agentic workflow validation failed.**\n\n';
        for (const [, title, advice] of failedStages) body += `**${title}:** ${advice}\n\n`;
        if (!failedStages.length) body += 'No recognized failed stage was available. Inspect the linked run for setup or runner errors.\n\n';
        if (failedStages.some(([id]) => id === 'drift')) {
            body += '```shell\ngh aw compile --purge --force-refresh-action-pins --validate --no-check-update\n```\n\n'
                + 'CI checks the merge result. If your branch is clean locally, reproduce with the current base incorporated; '
                + 'check compiler/source/action-pin alignment rather than editing generated files by hand.\n\n';
        }
    }
    body += `Configured compiler: ${compiler || 'unavailable'}.\n\n`
        + `[${run.conclusion === 'success' ? 'Successful validation run' : 'Failure details and available evidence'}](${runUrl})`
        + ` | Commit \`${run.head_sha.slice(0, 7)}\``;
    if (Buffer.byteLength(body) > limits.comment) throw new Error('Validation comment exceeded its size budget.');

    const comments = await github.paginate(github.rest.issues.listComments, { owner, repo, issue_number, per_page: 100 });
    const existing = comments.find(comment => comment.user?.login === 'github-actions[bot]' &&
        comment.user?.type === 'Bot' && comment.body?.startsWith(`[automated] ${marker}`));
    if (!existing && run.conclusion === 'success') return;
    if (!await current()) return skip('The validation became stale before comment publication.');
    try {
        if (existing) {
            await github.rest.issues.updateComment({ owner, repo, comment_id: existing.id, body });
        } else {
            await github.rest.issues.createComment({ owner, repo, issue_number, body });
        }
    } catch (error) {
        if (error.status !== 403) throw error;
        return skip('The token cannot write PR comments. Validation remains authoritative; no stronger credentials were requested.');
    }
    await core.summary.addRaw(`Validation comment ${existing ? 'updated' : 'created'} on PR #${issue_number}.\n\n[Validation run](${runUrl})`).write();
}

module.exports = { summarize, publish, driftEvidence, evidence, limits };
