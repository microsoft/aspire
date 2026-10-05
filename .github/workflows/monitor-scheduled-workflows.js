// Scheduled-workflow failure watchdog (.github/workflows/monitor-scheduled-workflows.yml).
//
// The reusable issue mechanics (marker dedup, the per-run comment-recording loop,
// octokit primitives) live in ./tracking-issue.js. This module owns the
// watchdog-specific bits: the marker namespace, issue title/body, the per-run
// failure comment, the record/close/noop decision (all pure), and the run()
// orchestrator that reads the watch list, polls each workflow, and files/closes
// issues. The pure helpers are unit-tested; run() is integration-tested via a fake.
//
// Contract: one dedup'd issue per *workflow file*, marker-based lookup, a failure
// comment per newly-observed failed run, and close-on-green.

'use strict';

const fs = require('node:fs');
const path = require('node:path');

const tracking = require('./tracking-issue.js');

const AUTOMATION_BROKEN_LABEL = 'automation-broken';

// First line of every managed issue body. Used to map an issue back to the
// workflow it tracks without relying on the (eventually-consistent) Search API.
//   <!-- automation-broken:generate-api-diffs.yml -->
const MARKER_PREFIX = 'automation-broken:';

// Conclusions that count as "the workflow is broken". `cancelled` is excluded:
// operator cancellation (and concurrency-superseded runs) is not a workflow defect,
// and firing on it would create noise. `timed_out` is NOT excluded — a run that
// hits its timeout is treated as broken (see BACKSTOP_CONCLUSIONS below).
const FAILURE_CONCLUSIONS = new Set(['failure', 'timed_out', 'startup_failure']);
const SUCCESS_CONCLUSIONS = new Set(['success']);
const WORKFLOW_RUN_PAGE_SIZE = 100;
// The watchdog runs every two hours. Look back three hours so ordinary GitHub
// schedule/queue delay cannot create a gap where a completed run is never seen.
const POLLING_WINDOW_MS = 3 * 60 * 60 * 1000;

// Backstop set for `selfReports` entries: workflows that file their own failure
// issues in-pipeline via an `if: failure()` reporter job. That reporter cannot
// catch two conclusions, so the watchdog backstops exactly these:
//   - startup_failure: the run never started a job, so the reporter job never ran.
//   - timed_out: a job-level timeout is *cancelled-class*, so `failure()` is false
//     and the reporter job does not run (see GitHub's status-check functions docs).
// Plain `failure` is deliberately EXCLUDED: the in-pipeline reporter owns it under
// its own `ci-failure:<file>:…` marker, so recording it here would file a second,
// duplicate issue under the watchdog's `automation-broken:<file>` marker.
const BACKSTOP_CONCLUSIONS = new Set(['startup_failure', 'timed_out']);

function buildMarker(workflowFile) {
    return `<!-- ${MARKER_PREFIX}${workflowFile} -->`;
}

// Parses the watch-list config (the parsed JSON object) and returns the entries
// that are enabled (missing `enabled` defaults to enabled). Disabled entries are
// dropped so an operator can stop watching a workflow by flipping one flag.
function selectEnabled(config) {
    const watched = Array.isArray(config?.watched) ? config.watched : [];
    return watched.filter(entry => entry && typeof entry.file === 'string' && entry.enabled !== false);
}

function buildIssueTitle(displayName) {
    return `Scheduled workflow failing: ${displayName}`;
}

// Builds the static issue body. Each failed run is recorded as a comment (see the
// runner), so the body is a fixed description written once at filing; the marker
// is embedded so the issue can be found again. The body is stamped autoClose:true
// — a watchdog-filed issue tracks "is this workflow currently broken", so a later
// green run resolves it (the watchdog closes it; the stamp also lets any future
// cross-producer closer do so).
//
// `selfReports` entries are backstop-only (startup_failure / timed_out): their
// normal failures are filed in-pipeline, so the body says so to avoid confusion
// with the in-pipeline issue.
function buildIssueBody({ marker, displayName, workflowFile, selfReports = false }) {
    const link = `[\`${workflowFile}\`](../../actions/workflows/${workflowFile})`;
    const lead = selfReports
        ? `The scheduled workflow ${link} (**${displayName}**) had a run that **failed to start or timed out**. Its normal failures are reported separately by an in-pipeline job; this issue backstops runs that never produced a result.`
        : `The scheduled workflow ${link} (**${displayName}**) is failing.`;

    return tracking.buildBody({
        marker,
        autoClose: true,
        lead,
        note: [
            'Filed and updated automatically by the scheduled-workflow watchdog. Each',
            'failed run is added as a comment below, and the issue is **closed',
            'automatically** on the next successful run.',
            'See [docs/ci/monitor-scheduled-workflows.md](../../blob/main/docs/ci/monitor-scheduled-workflows.md).',
        ],
    });
}

// Comment recorded per newly-observed failed run.
// failure: { runUrl, runNumber, sha, conclusion }
function formatComment({ runUrl, runNumber, sha, conclusion }) {
    const runLink = runUrl ? `[run #${runNumber ?? '?'}](${runUrl})` : `run #${runNumber ?? '?'}`;
    const shaPart = sha ? ` (commit \`${String(sha).slice(0, 8)}\`)` : '';

    return `The scheduled run concluded \`${conclusion}\` in ${runLink}${shaPart}.`;
}

function getRunTimestamp(run) {
    const value = run?.updated_at ?? run?.run_started_at ?? run?.created_at;
    if (typeof value !== 'string') {
        return null;
    }

    const timestamp = Date.parse(value);
    return Number.isNaN(timestamp) ? null : timestamp;
}

function selectRunsForPollingWindow(runs, { now = new Date(), pollingWindowMs = POLLING_WINDOW_MS } = {}) {
    const nowTimestamp = now.getTime();
    const cutoff = nowTimestamp - pollingWindowMs;
    return (runs ?? [])
        .filter(run => {
            const timestamp = getRunTimestamp(run);
            return timestamp !== null && timestamp >= cutoff && timestamp <= nowTimestamp;
        })
        .sort((left, right) => getRunTimestamp(left) - getRunTimestamp(right));
}

function formatIssueReference(issue) {
    return issue?.dryRunPlaceholder ? 'the new issue' : `issue #${issue.number}`;
}

// Decides what the watchdog should do for one workflow run conclusion and any
// existing open issue for it. Dedup of an already-recorded
// run is handled downstream by the shared engine (recordRun scans comments), so a
// still-failing run consistently resolves to 'record' and is then skipped if its
// comment already exists.
//
// `failureConclusions` selects which conclusions count as "broken": the full set
// for normal entries, or BACKSTOP_CONCLUSIONS for `selfReports` entries (which
// own their plain failures in-pipeline). A conclusion not in the failure set and
// not 'success' is a no-op — so for a `selfReports` entry a plain `failure` does
// nothing here (the in-pipeline reporter handles it).
//   action: 'record' | 'close' | 'noop'
function decideAction({ conclusion, issue, failureConclusions = FAILURE_CONCLUSIONS }) {
    const normalized = typeof conclusion === 'string' ? conclusion.toLowerCase() : null;

    if (normalized !== null && failureConclusions.has(normalized)) {
        return issue
            ? { action: 'record', reason: `latest run concluded '${normalized}'; recording on issue #${issue.number}` }
            : { action: 'record', reason: `latest run concluded '${normalized}'; no open issue` };
    }

    if (normalized !== null && SUCCESS_CONCLUSIONS.has(normalized)) {
        return issue
            ? { action: 'close', reason: `latest run concluded 'success'; closing issue #${issue.number}` }
            : { action: 'noop', reason: 'latest run succeeded; nothing open' };
    }

    // null (no completed run yet), 'cancelled', 'skipped', 'neutral', etc.
    return { action: 'noop', reason: `latest conclusion '${normalized ?? 'none'}' is not actionable` };
}

// Orchestrator. Reads the watch list next to this file, polls each workflow's
// recently completed scheduled runs, and files/comments/closes its issue. Invoked
// from an actions/github-script step. dryRun logs intended actions without
// mutating GitHub.
async function run({ github, context, core, dryRun = false, now = new Date() }) {
    const { owner, repo } = context.repo;
    const label = AUTOMATION_BROKEN_LABEL;
    const log = msg => core.info(`${dryRun ? '[dry-run] ' : ''}${msg}`);

    // Resolve the config next to this file so the read is independent of cwd.
    const configPath = path.join(__dirname, 'monitor-scheduled-workflows.config.json');
    const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
    const watched = selectEnabled(config);
    core.info(`Watching ${watched.length} workflow(s) from ${path.basename(configPath)}.`);

    if (dryRun) {
        log(`would ENSURE label '${label}' exists`);
    } else {
        await tracking.ensureLabel(github, owner, repo, {
            name: label, color: 'B60205', description: 'A scheduled/automation workflow is failing',
        });
    }

    // List once and reuse across watched workflows; each workflow's marker is
    // distinct, so a fresh issue filed for one cannot affect another's lookup.
    // Failure recording must include closed issues so a recurring failure reopens
    // the canonical tracker instead of filing a duplicate.
    const issues = await tracking.listIssuesByLabel(github, owner, repo, label);
    for (const wf of watched) {
        let recentRuns;
        try {
            // event: 'schedule' is required. Watched workflows commonly also have
            // workflow_dispatch (and some, e.g. warm-cli-e2e-image-cache.yml, a
            // push: trigger). Without this filter a completed run in the polling
            // window could be a manual or push run, so a manual/push success would
            // auto-close a real scheduled-failure issue (masking the silent failure
            // this watchdog exists to catch), and a manual/push failure would file
            // a false issue.
            const runs = await github.rest.actions.listWorkflowRuns({
                owner, repo, workflow_id: wf.file, branch: 'main', event: 'schedule', status: 'completed', per_page: WORKFLOW_RUN_PAGE_SIZE,
            });
            recentRuns = selectRunsForPollingWindow(runs.data.workflow_runs, { now });
        } catch (error) {
            core.warning(`Could not list runs for ${wf.file}: ${error.message}`);
            continue;
        }

        const marker = buildMarker(wf.file);
        if (recentRuns.length === 0) {
            const { action, reason } = decideAction({ conclusion: null, issue: tracking.findOpenIssueForMarker(issues, marker) });
            core.info(`${wf.file}: conclusion=none -> ${action} (${reason})`);
            continue;
        }

        const newest = recentRuns[recentRuns.length - 1];
        for (const latest of recentRuns) {
            const normalizedConclusion = typeof latest.conclusion === 'string' ? latest.conclusion.toLowerCase() : null;
            if (normalizedConclusion !== null && SUCCESS_CONCLUSIONS.has(normalizedConclusion)) {
                continue;
            }

            const issue = tracking.findOpenIssueForMarker(issues, marker);
            const conclusion = latest.conclusion;
            // `selfReports` entries own their plain failures in-pipeline; the watchdog
            // only backstops the conclusions that reporter cannot catch.
            const failureConclusions = wf.selfReports ? BACKSTOP_CONCLUSIONS : FAILURE_CONCLUSIONS;
            const { action, reason } = decideAction({ conclusion, issue, failureConclusions });

            core.info(`${wf.file}: run=${latest.id ?? '?'} conclusion=${conclusion ?? 'none'} -> ${action} (${reason})`);

            if (action === 'noop') {
                continue;
            }

            if (action === 'record') {
                const comment = formatComment({
                    runUrl: latest.html_url, runNumber: latest.run_number, sha: latest.head_sha, conclusion,
                });
                // Optional per-entry labels (e.g. area-cli, deployment-e2e) ride alongside
                // the lookup label; dedup keeps automation-broken single if also listed.
                const issueLabels = [...new Set([label, ...(wf.labels ?? [])])];
                const issueBody = buildIssueBody({ marker, displayName: wf.name, workflowFile: wf.file, selfReports: wf.selfReports === true });
                if (dryRun) {
                    const recordingIssue = tracking.findOpenIssueForMarker(issues, marker) ?? tracking.findIssueForMarker(issues, marker);
                    if (recordingIssue !== null &&
                        !recordingIssue.dryRunPlaceholder &&
                        await tracking.hasCommentForRun(github, owner, repo, recordingIssue.number, tracking.runMarker(latest.id))) {
                        core.info(`Run ${latest.id} already recorded in #${recordingIssue.number}; skipping duplicate comment.`);
                        continue;
                    }

                    log(`would RECORD failure for ${wf.file} on ${recordingIssue ? formatIssueReference(recordingIssue) : 'a new issue'}`);
                    if (recordingIssue === null) {
                        issues.push({ number: 0, body: issueBody, labels: issueLabels, state: 'open', dryRunPlaceholder: true });
                    } else if (recordingIssue.state === 'closed') {
                        recordingIssue.state = 'open';
                    }
                    continue;
                }
                const result = await tracking.recordRun(github, context, core, {
                    label, labels: issueLabels, marker, title: buildIssueTitle(wf.name),
                    runId: latest.id,
                    buildBody: () => issueBody,
                    comment, issues,
                });
                if (result.created) {
                    issues.push({ number: result.number, body: issueBody, labels: issueLabels, state: 'open' });
                } else if (result.reopened) {
                    const recordedIssue = tracking.findIssueForMarker(issues, marker);
                    if (recordedIssue !== null) {
                        recordedIssue.state = 'open';
                    }
                }

                if (!result.skipped) {
                    core.info(`${result.created ? 'Filed' : 'Updated'} #${result.number} for ${wf.file}`);
                }
                continue;
            }
        }

        const newestConclusion = typeof newest.conclusion === 'string' ? newest.conclusion.toLowerCase() : null;
        if (newestConclusion !== null && SUCCESS_CONCLUSIONS.has(newestConclusion)) {
            const issue = tracking.findOpenIssueForMarker(issues, marker);
            const failureConclusions = wf.selfReports ? BACKSTOP_CONCLUSIONS : FAILURE_CONCLUSIONS;
            const { action, reason } = decideAction({ conclusion: newest.conclusion, issue, failureConclusions });
            core.info(`${wf.file}: newest run=${newest.id ?? '?'} conclusion=${newest.conclusion ?? 'none'} -> ${action} (${reason})`);

            if (action !== 'close') {
                continue;
            }

            const autoClose = tracking.readAutoClose(issue.body);
            if (autoClose !== true) {
                core.info(`Issue #${issue.number} for ${wf.file} does not opt into auto-close; leaving it open.`);
                continue;
            }

            if (dryRun) {
                log(`would CLOSE ${formatIssueReference(issue)} (${wf.file})`);
                issue.state = 'closed';
                continue;
            }
            await tracking.closeIssue(github, owner, repo, issue.number);
            await tracking.addComment(github, owner, repo, issue.number,
                `Latest run succeeded ([run #${newest.run_number}](${newest.html_url})). Closing automatically.`);
            issue.state = 'closed';
            core.info(`Closed #${issue.number} for ${wf.file}`);
        }
    }
}

module.exports = {
    AUTOMATION_BROKEN_LABEL,
    MARKER_PREFIX,
    FAILURE_CONCLUSIONS,
    BACKSTOP_CONCLUSIONS,
    SUCCESS_CONCLUSIONS,
    buildMarker,
    buildIssueTitle,
    selectEnabled,
    buildIssueBody,
    formatComment,
    selectRunsForPollingWindow,
    decideAction,
    run,
};
