const fs = require('node:fs/promises');
const path = require('node:path');
const dispatcher = require('../../../.github/workflows/auto-rerun-transient-ci-failures.js');
const pullRequest = require('../../../.github/workflows/auto-rerun/rerun-pull-request.js');
const mainPolicy = require('../../../.github/workflows/auto-rerun/rerun-main.js');
const common = require('../../../.github/workflows/auto-rerun/common.js');
const rerunWorkflow = {
    ...common, ...pullRequest, ...mainPolicy, ...dispatcher,
    // Existing fixtures cover each policy's execution independently.
    rerunMatchedJobs: options => options.sourceRunScope === 'main'
        ? mainPolicy.rerunMainFailures(options)
        : pullRequest.rerunPullRequestFailures(options),
};

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
        case 'selectPolicy':
            return rerunWorkflow.selectPolicy(payload);

        case 'dispatchWorkflow': {
            const requests = [];
            const outputs = {};
            const messages = [];
            const summary = new SummaryRecorder();
            const github = createGitHubRecorder(payload, requests);
            github.rest = {
                actions: {
                    getWorkflowRun: async options => {
                        requests.push({ route: 'getWorkflowRun', payload: options });
                        return { data: payload.workflowRun };
                    },
                },
            };
            const core = {
                summary,
                setOutput: (name, value) => { outputs[name] = value; },
                info: message => messages.push(message),
                warning: message => messages.push(message),
                setFailed: message => messages.push(message),
            };
            const context = {
                repo: { owner: payload.owner ?? 'microsoft', repo: payload.repo ?? 'aspire' },
                eventName: payload.eventName ?? 'workflow_run',
                payload: { workflow_run: payload.workflowRun },
            };
            const originalFetch = global.fetch;
            global.fetch = async url => {
                const match = String(url).match(/\/actions\/jobs\/(\d+)\/logs$/);
                if (!match) {
                    throw new Error(`Unexpected fetch request: ${url}`);
                }

                return {
                    ok: true,
                    status: 200,
                    text: async () => payload.jobLogTextByJobId?.[match[1]] ?? '',
                };
            };

            try {
                const analysis = await dispatcher.run({
                    phase: 'analyze',
                    github, core, context,
                    runId: payload.runId,
                    dryRun: payload.dryRun ?? false,
                    forceRerunAll: payload.forceRerunAll ?? false,
                    workspace: path.resolve(__dirname, '../../..'),
                });
                const analysisRequests = [...requests];
                if (analysis?.executionEligible && payload.execute) {
                    await dispatcher.run({
                        phase: 'execute', github, core, context,
                        analysis,
                    });
                }
                return { analysis, analysisRequests, requests, outputs, messages, events: summary.events };
            }
            finally {
                global.fetch = originalFetch;
            }
        }

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
                    retryPatternsConfig: payload.retryPatternsConfig ?? null,
                });

                return { ...result, logRequestJobIds };
            }

        case 'analyzeMainFailedJobs':
            {
                const annotationRequestJobIds = [];
                const logRequestJobIds = [];
                const result = await rerunWorkflow.analyzeMainFailedJobs({
                    jobs: payload.jobs ?? [],
                    getAnnotationsForJob: async job => {
                        annotationRequestJobIds.push(job.id);
                        return payload.annotationTextByJobId?.[String(job.id)] ?? '';
                    },
                    getJobLogTextForJob: async job => {
                        logRequestJobIds.push(job.id);
                        return payload.jobLogTextByJobId?.[String(job.id)] ?? '';
                    },
                    maxRetryableJobs: payload.maxRetryableJobs,
                    runAttempt: payload.runAttempt,
                });

                return { ...result, annotationRequestJobIds, logRequestJobIds };
            }

        case 'formatMatchedPatternForMarkdown':
            return rerunWorkflow.formatMatchedPatternForMarkdown(payload.matchedPattern);

        case 'promoteTestExecutionFailureJobs':
            return rerunWorkflow.promoteTestExecutionFailureJobs(
                payload.retryableJobs ?? [],
                payload.skippedJobs ?? [],
                payload.allMatchedTests ?? []);

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

        case 'getDefaultMaxRunAttempt':
            return rerunWorkflow.defaultMaxRunAttempt;

        case 'getMainMaxRunAttempt':
            return rerunWorkflow.mainMaxRunAttempt;

        case 'validateRetryPatternsConfig':
            return rerunWorkflow.validateRetryPatternsConfig(payload.config);

        case 'loadRetryPatternsConfig':
            return rerunWorkflow.loadRetryPatternsConfig(payload.configPath);

        case 'extractFailedTestsFromTrx':
            return rerunWorkflow.extractFailedTestsFromTrx(payload.trxContent);

        case 'matchesRetryPattern':
            return rerunWorkflow.matchesRetryPattern(payload.text, payload.patternValue);

        case 'matchTestFailurePatterns':
            return rerunWorkflow.matchTestFailurePatterns(
                payload.failedTests,
                payload.testProject,
                payload.patterns);

        case 'matchJobLogPattern':
            return rerunWorkflow.matchJobLogPattern(
                payload.jobName,
                payload.jobLogText,
                payload.patterns);

        case 'analyzeTrxFiles':
            return rerunWorkflow.analyzeTrxFiles(
                payload.trxFileContents,
                payload.testFailurePatterns);

        case 'selectTestResultsArtifact':
            return rerunWorkflow.selectTestResultsArtifact(payload.artifacts);

        case 'hasTestExecutionFailureStep':
            return rerunWorkflow.hasTestExecutionFailureStep(payload.failedSteps ?? []);

        case 'validateRetryPatternsConfigFromFile': {
            const configPath = path.resolve(payload.configPath);
            const result = rerunWorkflow.loadRetryPatternsConfig(configPath);
            return result;
        }

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

            const returnValue = await rerunWorkflow.rerunMatchedJobs({
                ...payload,
                github,
                summary,
                delay: async () => {},
            });

            return { requests, events: summary.events, returnValue };
        }

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

function createGitHubRecorder(payload, requests) {
    let currentRunRequestCount = 0;
    let mainRefRequestCount = 0;
    let mainWorkflowRunsRequestCount = 0;

    const github = {
        request: async (route, requestPayload) => {
            requests.push({ route, payload: requestPayload });

            if (route === 'GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs') {
                const pages = payload.jobsByPage;
                const page = Number(requestPayload.page ?? 1);
                const jobs = Array.isArray(pages) ? pages[page - 1] ?? [] : payload.jobs ?? [];
                const hasNextPage = Array.isArray(pages) && page < pages.length;
                return {
                    data: {
                        total_count: Array.isArray(pages) ? pages.flat().length : jobs.length,
                        jobs,
                    },
                    headers: {
                        link: hasNextPage ? '<https://api.github.com/next>; rel="next"' : '',
                    },
                };
            }
            if (route === 'GET /repos/{owner}/{repo}/check-runs/{check_run_id}/annotations') {
                return { data: payload.annotations ?? [], headers: {} };
            }
            if (route === 'GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts') {
                const pages = payload.artifactsByPage;
                const page = Number(requestPayload.page ?? 1);
                const artifacts = Array.isArray(pages) ? pages[page - 1] ?? [] : payload.artifacts ?? [];
                const hasNextPage = Array.isArray(pages) && page < pages.length;
                return {
                    data: {
                        total_count: Array.isArray(pages) ? pages.flat().length : artifacts.length,
                        artifacts,
                    },
                    headers: {
                        link: hasNextPage ? '<https://api.github.com/next>; rel="next"' : '',
                    },
                };
            }

            if (payload.failedRequestRoutes?.includes(route)) {
                throw new Error(`Simulated request failure for ${route}`);
            }

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
                const currentRun = Array.isArray(payload.currentRunResponses)
                    ? payload.currentRunResponses[
                        Math.min(currentRunRequestCount++, payload.currentRunResponses.length - 1)]
                    : payload.currentRun;
                return {
                    data: currentRun ? {
                        status: 'completed',
                        conclusion: 'failure',
                        ...currentRun,
                    } : {
                        run_attempt: payload.latestRunAttempt ?? null,
                    },
                };
            }

            if (route === 'GET /repos/{owner}/{repo}/git/ref/{ref}') {
                const currentMainSha = Array.isArray(payload.currentMainShas)
                    ? payload.currentMainShas[Math.min(mainRefRequestCount++, payload.currentMainShas.length - 1)]
                    : payload.currentMainSha;
                return {
                    data: {
                        object: {
                            sha: currentMainSha ?? null,
                        },
                    },
                };
            }

            if (route === 'GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs') {
                const workflowRuns = Array.isArray(payload.mainWorkflowRunResponses)
                    ? payload.mainWorkflowRunResponses[
                        Math.min(mainWorkflowRunsRequestCount++, payload.mainWorkflowRunResponses.length - 1)]
                    : payload.mainWorkflowRuns;
                return {
                    data: {
                        workflow_runs: workflowRuns ?? [],
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
        paginate: async function (route, requestPayload, mapFunction) {
            const results = [];

            for (let page = 1; ; page++) {
                const response = await this.request(route, { ...requestPayload, page });
                const data = response.data;
                const nestedItems = data && typeof data === 'object' && !Array.isArray(data)
                    ? Object.values(data).find(Array.isArray)
                    : undefined;
                const normalizedResponse = Array.isArray(data) || !nestedItems
                    ? response
                    : { ...response, data: nestedItems };

                const items = mapFunction
                    ? mapFunction(normalizedResponse)
                    : normalizedResponse.data;
                results.push(...items);

                if (!response.headers?.link?.includes('rel="next"')) {
                    return results;
                }
            }
        },
    };

    return github;
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
