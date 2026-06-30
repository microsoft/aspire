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

// Decides what the watchdog should do for one workflow given its latest completed
// run conclusion and any existing open issue for it. Dedup of an already-recorded
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
// latest completed scheduled run, and files/comments/closes its issue. Invoked
// from an actions/github-script step. dryRun logs intended actions without
// mutating GitHub.
async function run({ github, context, core, dryRun = false }) {
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
    const openIssues = await tracking.listOpenIssuesByLabel(github, owner, repo, label);

    for (const wf of watched) {
        let latest;
        try {
            // event: 'schedule' is required. Watched workflows commonly also have
            // workflow_dispatch (and some, e.g. warm-cli-e2e-image-cache.yml, a
            // push: trigger). Without this filter the "latest completed run" could
            // be a manual or push run, so a manual/push success would auto-close a
            // real scheduled-failure issue (masking the silent failure this watchdog
            // exists to catch), and a manual/push failure would file a false issue.
            const runs = await github.rest.actions.listWorkflowRuns({
                owner, repo, workflow_id: wf.file, branch: 'main', event: 'schedule', status: 'completed', per_page: 1,
            });
            latest = runs.data.workflow_runs[0];
        } catch (error) {
            core.warning(`Could not list runs for ${wf.file}: ${error.message}`);
            continue;
        }

        const marker = buildMarker(wf.file);
        const issue = tracking.findOpenIssueForMarker(openIssues, marker);
        const conclusion = latest ? latest.conclusion : null;
        // `selfReports` entries own their plain failures in-pipeline; the watchdog
        // only backstops the conclusions that reporter cannot catch.
        const failureConclusions = wf.selfReports ? BACKSTOP_CONCLUSIONS : FAILURE_CONCLUSIONS;
        const { action, reason } = decideAction({ conclusion, issue, failureConclusions });

        core.info(`${wf.file}: conclusion=${conclusion ?? 'none'} -> ${action} (${reason})`);

        if (action === 'noop') {
            continue;
        }

        if (action === 'record') {
            const comment = formatComment({
                runUrl: latest.html_url, runNumber: latest.run_number, sha: latest.head_sha, conclusion,
            });
            if (dryRun) {
                log(`would RECORD failure for ${wf.file} on ${issue ? `issue #${issue.number}` : 'a new issue'}`);
                continue;
            }
            // Optional per-entry labels (e.g. area-cli, deployment-e2e) ride alongside
            // the lookup label; dedup keeps automation-broken single if also listed.
            const issueLabels = [...new Set([label, ...(wf.labels ?? [])])];
            const result = await tracking.recordRun(github, context, core, {
                label, labels: issueLabels, marker, title: buildIssueTitle(wf.name),
                runId: latest.id,
                buildBody: () => buildIssueBody({ marker, displayName: wf.name, workflowFile: wf.file, selfReports: wf.selfReports === true }),
                comment, openIssues,
            });
            if (!result.skipped) {
                core.info(`${result.created ? 'Filed' : 'Updated'} #${result.number} for ${wf.file}`);
            }
            continue;
        }

        if (action === 'close') {
            if (dryRun) {
                log(`would CLOSE issue #${issue.number} (${wf.file})`);
                continue;
            }
            await tracking.addComment(github, owner, repo, issue.number,
                `Latest run succeeded ([run #${latest.run_number}](${latest.html_url})). Closing automatically.`);
            await tracking.closeIssue(github, owner, repo, issue.number);
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
    decideAction,
    run,
};
