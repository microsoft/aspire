const fs = require('node:fs/promises');
const rerunWorkflow = require('../../../.github/workflows/auto-rerun-transient-ci-failures.js');

class SummaryRecorder {
    constructor() {
        this.events = [];
    }

    addHeading(text, level = 1) {
        this.events.push({ type: 'heading', text, level });
        return this;
    }

    addTable(rows) {
        this.events.push({ type: 'table', rows });
        return this;
    }

    addRaw(text, addEol = false) {
        this.events.push({ type: 'raw', text, addEol });
        return this;
    }

    addLink(text, href) {
        this.events.push({ type: 'link', text, href });
        return this;
    }

    addBreak() {
        this.events.push({ type: 'break' });
        return this;
    }

    async write() {
        this.events.push({ type: 'write' });
        return this;
    }
}

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
        case 'analyzeFailedJobs':
            {
                const logRequestJobIds = [];
                const result = await rerunWorkflow.analyzeFailedJobs({
                    jobs: payload.jobs ?? [],
                    getAnnotationsForJob: async job => payload.annotationTextByJobId?.[String(job.id)] ?? '',
                    getJobLogTextForJob: async job => {
                        logRequestJobIds.push(job.id);
                        return payload.jobLogTextByJobId?.[String(job.id)] ?? '';
                    },
                    maxRetryableJobs: payload.maxRetryableJobs,
                });

                return { ...result, logRequestJobIds };
            }

        case 'formatMatchedPatternForMarkdown':
            return rerunWorkflow.formatMatchedPatternForMarkdown(payload.matchedPattern);

        case 'getCheckRunIdForJob':
            return rerunWorkflow.getCheckRunIdForJob({
                job: payload.job,
                getJobForWorkflowRun: payload.workflowJob ? async () => payload.workflowJob : undefined,
            });

        case 'getAssociatedPullRequestNumbers': {
            const requests = [];
            const github = createGitHubRecorder(payload, requests);
            const pullRequestNumbers = await rerunWorkflow.getAssociatedPullRequestNumbers({
                github,
                owner: payload.owner ?? 'dotnet',
                repo: payload.repo ?? 'aspire',
                workflowRun: payload.workflowRun,
            });

            return { pullRequestNumbers, requests };
        }

        case 'computeRerunEligibility':
            return rerunWorkflow.computeRerunEligibility(payload);

        case 'computeRerunExecutionEligibility':
            return rerunWorkflow.computeRerunExecutionEligibility(payload);

        case 'writeAnalysisSummary': {
            const summary = new SummaryRecorder();
            await rerunWorkflow.writeAnalysisSummary({
                ...payload,
                summary,
            });

            return { events: summary.events };
        }

        case 'rerunMatchedJobs': {
            const requests = [];
            const summary = new SummaryRecorder();
            const github = createGitHubRecorder(payload, requests);

            await rerunWorkflow.rerunMatchedJobs({
                ...payload,
                github,
                summary,
            });

            return { requests, events: summary.events };
        }

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

function createGitHubRecorder(payload, requests) {
    return {
        request: async (route, requestPayload) => {
            requests.push({ route, payload: requestPayload });

            if (route === 'GET /repos/{owner}/{repo}/issues/{issue_number}') {
                const issueNumber = String(requestPayload.issue_number);
                const state = payload.issueStatesByNumber?.[issueNumber] ?? 'closed';
                return {
                    data: {
                        state,
                        pull_request: {
                            url: `https://api.github.com/repos/${requestPayload.owner}/${requestPayload.repo}/pulls/${issueNumber}`,
                        },
                    },
                };
            }

            if (route === 'GET /repos/{owner}/{repo}/actions/runs/{run_id}') {
                return {
                    data: {
                        run_attempt: payload.latestRunAttempt ?? null,
                    },
                };
            }

            if (route === 'GET /repos/{owner}/{repo}/pulls') {
                if (payload.failPullRequestLookup) {
                    throw new Error(payload.failPullRequestLookup);
                }

                const page = Number(requestPayload.page ?? 1);
                const pullRequestPages = payload.pullRequestsByHeadPages?.[requestPayload.head];
                const pullRequests = pullRequestPages
                    ? pullRequestPages[page - 1] ?? []
                    : payload.pullRequestsByHead?.[requestPayload.head] ?? [];
                const hasNextPage = Array.isArray(pullRequestPages) && page < pullRequestPages.length;

                return {
                    data: pullRequests,
                    headers: {
                        link: hasNextPage ? '<https://api.github.com/next>; rel="next"' : '',
                    },
                };
            }
            if (route === 'POST /repos/{owner}/{repo}/issues/{issue_number}/comments') {
                const issueNumber = String(requestPayload.issue_number);
                const htmlUrl = payload.commentHtmlUrlByNumber?.[issueNumber] ?? null;

                return {
                    data: {
                        html_url: htmlUrl,
                    },
                };
            }

            return { data: {} };
        },
    };
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
