// Dispatch CI rerun analysis and execution to the appropriate policy.
// The workflow keeps these phases in separate jobs to give analysis a read-only
// GITHUB_TOKEN. Permissions cannot be conditional or escalated between steps:
// https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idpermissions
//
// PR/manual analysis currently enables force mode: no classification, an open PR,
// and at most three automatic retries. Main never uses force mode: every real
// failure must match its narrow allowlist, with at most one retry of current main.
const pullRequest = require('./auto-rerun/rerun-pull-request.js');
const main = require('./auto-rerun/rerun-main.js');
const common = require('./auto-rerun/common.js');

function selectPolicy({ eventName, owner, repo, workflowRun }) {
    if (owner !== 'microsoft' || !workflowRun || (workflowRun.name && workflowRun.name !== 'CI')) {
        return null;
    }

    // Manual dispatch retains the PR-associated policy, even when a supplied failed
    // run originated from a push. It must not enable the automatic current-main policy.
    if (eventName === 'workflow_dispatch' &&
        workflowRun.status === 'completed' &&
        workflowRun.conclusion === 'failure') {
        return 'pull-request';
    }

    if (eventName !== 'workflow_run' || workflowRun.conclusion !== 'failure') {
        return null;
    }

    if (workflowRun.event === 'pull_request') {
        return 'pull-request';
    }

    if (repo === 'aspire' && workflowRun.event === 'push' && workflowRun.head_branch === 'main') {
        return 'main';
    }

    return null;
}

async function analyze({ github, core, context, runId, dryRun, forceRerunAll, workspace, token }) {
    const { owner, repo } = context.repo;
    const workflowRun = context.eventName === 'workflow_dispatch'
        ? (await github.rest.actions.getWorkflowRun({ owner, repo, run_id: parseRunId(runId) })).data
        : context.payload.workflow_run;
    const policy = selectPolicy({ ...context.repo, eventName: context.eventName, workflowRun });
    if (!policy) {
        const message = 'The source run is not a supported completed failed CI run. No jobs were rerun.';
        core.info(message);
        await common.writeRerunOutcomeSummary({
            summary: core.summary,
            sourceRunUrl: workflowRun.html_url,
            sourceRunAttempt: workflowRun.run_attempt,
            message,
        });
        core.setOutput('rerun_execution_eligible', 'false');
        return;
    }

    const options = {
        github, core, owner, repo, workflowRun, eventName: context.eventName,
        dryRun, forceRerunAll, workspace, token,
    };
    const analysis = policy === 'main'
        ? await main.analyzeMainFailures(options)
        : await pullRequest.analyzePullRequestFailures(options);
    analysis.eventName = context.eventName;
    core.setOutput('analysis', JSON.stringify(analysis));
    core.setOutput('rerun_execution_eligible', String(analysis.executionEligible));
    return analysis;
}

async function execute({ github, core, context, analysis }) {
    const { owner, repo } = context.repo;
    const policy = selectPolicy({
        owner, repo, eventName: analysis.eventName, workflowRun: analysis.run,
    });
    if (!policy || policy !== analysis.policy || analysis.eventName !== context.eventName) {
        throw new Error('The analysis does not match a supported CI rerun policy.');
    }
    if (!analysis.executionEligible || analysis.dryRun) {
        core.info('The analyzed run is not eligible for execution. Skipping.');
        return;
    }

    const options = {
        github, owner, repo, summary: core.summary,
        log: message => core.info(message),
        sourceRunId: analysis.run.id,
        sourceRunUrl: analysis.run.html_url,
        sourceRunAttempt: analysis.run.run_attempt,
        sourceHeadSha: analysis.run.head_sha,
        retryableJobs: analysis.retryableJobs,
    };
    if (policy === 'main') {
        const state = await main.rerunMainFailures({
            ...options,
            maxRunAttempt: main.mainMaxRunAttempt,
        });
        core.info(`Current-main rerun: ${state.outcome} (${state.reason}).`);
        if (state.outcome === 'failed') {
            core.setFailed(`Current-main rerun failed: ${state.reason}.`);
        }
        return state;
    }

    return pullRequest.rerunPullRequestFailures({
        ...options,
        pullRequestNumbers: analysis.pullRequestNumbers,
        testPatternMatchedTests: analysis.testPatternMatchedTests,
        forceRerunAll: analysis.forceRerunAll,
    });
}

function parseRunId(value) {
    const runId = Number(value);
    if (!Number.isInteger(runId) || runId <= 0) {
        throw new Error('workflow_dispatch requires a valid run_id input.');
    }
    return runId;
}

async function run({ phase, ...options }) {
    switch (phase) {
        case 'analyze':
            return analyze(options);
        case 'execute':
            return execute(options);
        default:
            throw new Error(`Unsupported rerun phase '${phase}'.`);
    }
}

module.exports = { run, selectPolicy };
