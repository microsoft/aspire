// Attempt-scoped job, annotation, and log access shared by both policies.
const common = require('./common.js');

function createJobReader({ github, owner, repo, core, token }) {
    const rerunWorkflow = common;
    const maxJobLogInspectionBytes = 256 * 1024;
    const paginate = (route, parameters, selectItems) => github.paginate(
        route,
        { ...parameters, per_page: 100 },
        response => Array.isArray(response.data) ? response.data : selectItems(response.data));

    async function listJobsForAttempt(runId, attemptNumber) {
        return paginate(
            'GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs',
            {
                owner,
                repo,
                run_id: runId,
                attempt_number: attemptNumber,
            },
            data => data.jobs || []);
    }

    async function listAnnotations(job) {
        try {
            const checkRunId = await rerunWorkflow.getCheckRunIdForJob({
                job,
                getJobForWorkflowRun: async jobId => {
                    const response = await github.rest.actions.getJobForWorkflowRun({
                        owner,
                        repo,
                        job_id: jobId,
                    });

                    return response.data;
                },
            });

            if (!checkRunId) {
                core.warning(`Unable to resolve a check run id for job ${job.id}.`);
                return [];
            }

            return await paginate(
                'GET /repos/{owner}/{repo}/check-runs/{check_run_id}/annotations',
                {
                    owner,
                    repo,
                    check_run_id: checkRunId,
                },
                data => Array.isArray(data) ? data : []);
        }
        catch (error) {
            core.warning(`Failed to list annotations for job ${job.id}: ${error.message}`);
            return [];
        }
    }

    async function getJobLogText(jobId) {
        try {
            const response = await fetch(`https://api.github.com/repos/${owner}/${repo}/actions/jobs/${jobId}/logs`, {
                headers: {
                    authorization: `Bearer ${token}`,
                    accept: 'application/vnd.github+json',
                    'x-github-api-version': '2022-11-28',
                },
            });

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }

            return (await response.text()).slice(-maxJobLogInspectionBytes);
        }
        catch (error) {
            core.warning(`Failed to fetch logs for job ${jobId}: ${error.message}`);
            return '';
        }
    }

    return { paginate, listJobsForAttempt, listAnnotations, getJobLogText };
}

module.exports = { createJobReader };
