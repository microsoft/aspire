// Test harness for the comment_selection job's inline github-script in
// .github/workflows/tests.yml.
//
// That script intentionally cannot be extracted into a requireable module: its job is granted
// pull-requests:write and deliberately never checks out the repo, so loading a repo file there would
// run PR-authored code under the write token (the escalation the job's design avoids). Instead the
// C# test extracts the *shipped* script text from the workflow and hands it here, and this harness
// executes it against mocked github/context/core -- exercising the real behavior (new comment,
// head-commit link, skip-on-missing) without changing the workflow's security posture.
const fs = require('node:fs/promises');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    // Write the result to a file (argv[3]) rather than stdout when provided, so a stray stderr/stdout
    // line from node (e.g. a deprecation warning) can't pollute the JSON the C# test parses.
    const outputPath = process.argv[3];

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));

    // The script reads the summary path from this env var, mirroring the job's `env:` block.
    if (request.commentFile) {
        process.env.SELECT_TESTS_COMMENT_FILE = request.commentFile;
    } else {
        delete process.env.SELECT_TESTS_COMMENT_FILE;
    }

    // Existing PR comments the script will page through to decide create-vs-update and what to
    // collapse. Each item mirrors the REST shape the script reads: { id, node_id, body }.
    const existingComments = request.existingComments ?? [];
    let nextCommentId = request.nextCommentId ?? 9000;
    const context = request.context;
    // The PR head the script sees via pulls.get -- the live head for the minimize gate. Defaults to
    // this run's own head SHA, i.e. "this run is for the current head" (the common case). Tests set a
    // different value to simulate a stale re-run (an older run replayed after a newer commit).
    const liveHeadSha = request.liveHeadSha ?? (context.payload.pull_request?.head?.sha ?? context.sha);

    const calls = { created: [], updated: [], minimized: [], infos: [] };
    const github = {
        // The script pages comments via github.paginate(github.rest.issues.listComments, ...).
        paginate: async () => existingComments,
        rest: {
            issues: {
                listComments: async () => existingComments,
                createComment: async args => {
                    const id = nextCommentId++;
                    calls.created.push({ id, body: args.body });
                    return { data: { id } };
                },
                updateComment: async args => { calls.updated.push({ commentId: args.comment_id, body: args.body }); },
            },
            // The script resolves the PR's current head via pulls.get to gate minimization.
            pulls: {
                get: async () => ({ data: { head: { sha: liveHeadSha } } }),
            },
        },
        // minimizeComment is GraphQL-only; capture the node_id the script asks to collapse.
        graphql: async (query, variables) => { calls.minimized.push(variables.id); },
    };
    const core = {
        info: message => { calls.infos.push(String(message)); },
        warning: () => {},
        error: () => {},
        setFailed: message => { throw new Error(String(message)); },
    };

    // github-script wraps the script body in an async function, so top-level await/return work; mirror
    // that with AsyncFunction and inject the same names the script references that github-script
    // provides (require, process, github, context, core).
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    const run = new AsyncFunction('require', 'process', 'github', 'context', 'core', request.script);
    await run(require, process, github, context, core);

    const json = JSON.stringify({ result: calls });
    if (outputPath) {
        await fs.writeFile(outputPath, json);
    } else {
        process.stdout.write(json);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
