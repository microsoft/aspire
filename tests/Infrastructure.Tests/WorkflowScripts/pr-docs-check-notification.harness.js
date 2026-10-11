// Execute the shipped inline github-script with the same plain Node runtime as
// Actions, without loading repository code into the privileged notification job.
const fs = require('node:fs/promises');

async function main() {
    const request = JSON.parse(await fs.readFile(process.argv[2], 'utf8'));
    process.env.CANONICAL_OUTCOME_PATH = request.outcomeFile;
    process.env.DRAFT_PR_URL = 'https://github.com/microsoft/aspire.dev/pull/1531';
    process.env.DRAFT_PR_NUMBER = '1531';
    process.env.GITHUB_SERVER_URL = 'https://github.com';
    process.env.GITHUB_REPOSITORY = 'microsoft/aspire';
    process.env.GITHUB_RUN_ID = '1234';

    const result = { attempts: [], warnings: [], summary: '', summaryWrites: 0, error: null };
    const github = {
        paginate: async () => [],
        rest: {
            issues: {
                listComments: async () => [],
                createComment: async args => {
                    result.attempts.push({
                        owner: args.owner,
                        repo: args.repo,
                        issueNumber: args.issue_number,
                        body: args.body,
                    });
                    if (request.errorStatus) {
                        throw Object.assign(new Error(request.errorMessage), {
                            status: request.errorStatus,
                            response: { data: { message: request.errorMessage } },
                        });
                    }
                    return { data: { id: 1 } };
                },
            },
        },
    };
    const core = {
        warning: message => result.warnings.push(message),
        info: () => {},
        summary: {
            addRaw: text => {
                result.summary += text;
                return core.summary;
            },
            write: async () => { result.summaryWrites++; },
        },
    };
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    const run = new AsyncFunction('require', 'process', 'github', 'core', request.script);
    try {
        await run(require, process, github, core);
    } catch (error) {
        result.error = error.message;
    }
    await fs.writeFile(process.argv[3], JSON.stringify(result));
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
