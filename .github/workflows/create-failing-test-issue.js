function parseCommand(body, defaultSourceUrl = null) {
    const match = /^\/create-issue(?:\s+(?<args>.+))?$/m.exec(body ?? '');
    if (!match) {
        return { success: false, errorMessage: 'No /create-issue command was found in the comment.' };
    }

    const { tokenizeArguments } = require('./workflow-command-helpers.js');
    let tokens;
    try {
        tokens = tokenizeArguments(match.groups?.args ?? '');
    }
    catch (error) {
        return { success: false, errorMessage: error.message };
    }

    const result = {
        success: true,
        testQuery: '',
        sourceUrl: defaultSourceUrl,
        workflow: 'ci',
        forceNew: false,
        listOnly: false,
    };

    const hasFlags = tokens.some(token => token.startsWith('--'));
    if (hasFlags) {
        for (let index = 0; index < tokens.length; index++) {
            const token = tokens[index];

            switch (token) {
                case '--test':
                    if (index + 1 >= tokens.length) {
                        return { success: false, errorMessage: 'Missing value for --test.' };
                    }

                    if (result.testQuery) {
                        return {
                            success: false,
                            errorMessage: 'Positional input is ambiguous. Use /create-issue --test "<test-name>" [--url <pr|run|job-url>] [--workflow <selector>] [--force-new].',
                        };
                    }

                    result.testQuery = tokens[++index];
                    break;

                case '--url':
                    if (index + 1 >= tokens.length) {
                        return { success: false, errorMessage: 'Missing value for --url.' };
                    }

                    result.sourceUrl = tokens[++index];
                    break;

                case '--workflow':
                    if (index + 1 >= tokens.length) {
                        return { success: false, errorMessage: 'Missing value for --workflow.' };
                    }

                    result.workflow = tokens[++index];
                    break;

                case '--force-new':
                    result.forceNew = true;
                    break;

                default:
                    if (token.startsWith('--')) {
                        return {
                            success: false,
                            errorMessage: `Unknown argument '${token}'. Supported arguments are --test, --url, --workflow, and --force-new.`,
                        };
                    }

                    if (result.testQuery) {
                        return {
                            success: false,
                            errorMessage: 'Positional input is ambiguous. Use /create-issue --test "<test-name>" [--url <pr|run|job-url>] [--workflow <selector>] [--force-new].',
                        };
                    }

                    result.testQuery = token;
                    break;
            }
        }

        if (!result.testQuery) {
            result.listOnly = true;
        }

        return result;
    }

    if (tokens.length === 0) {
        result.listOnly = true;
        return result;
    }

    if (tokens.length === 1) {
        result.testQuery = tokens[0];
        return result;
    }

    const candidateUrl = tokens[tokens.length - 1];
    if (isSupportedSourceUrl(candidateUrl)) {
        result.sourceUrl = candidateUrl;
        result.testQuery = tokens.slice(0, -1).join(' ');
        return result;
    }

    return {
        success: false,
        errorMessage: 'Positional input is ambiguous. Use /create-issue --test "<test-name>" [--url <pr|run|job-url>] [--workflow <selector>] [--force-new].',
    };
}

function formatListResponse(resolverOutcome, resultJson) {
    if (resolverOutcome === 'failure' && !resultJson) {
        return { error: true, message: 'The failing-test resolver failed to run.' };
    }

    const tests = resultJson?.allFailures?.tests?.map(t => t.canonicalTestName ?? t.displayTestName)
        ?? resultJson?.diagnostics?.availableFailedTests
        ?? [];

    if (tests.length > 0) {
        return {
            error: false,
            message: '**Failed tests found on this PR:**\n\n'
                + tests.map(name => `- \`/create-issue ${name}\``).join('\n')
                + '\n\n',
            tests,
        };
    }

    // The C# tool writes JSON even on failure (exits non-zero). Surface
    // the error instead of a misleading "no failures found" message.
    if (resolverOutcome === 'failure' || resultJson?.success === false) {
        const detail = resultJson?.errorMessage;
        return { error: true, message: detail ?? 'The failing-test resolver failed to run.' };
    }

    return { error: false, message: 'No test failures were found. Use `--url` to point to a specific workflow run.\n\n' };
}

function buildIssueSearchQuery(owner, repo, metadataMarker) {
    const escapedMarker = String(metadataMarker ?? '').replaceAll('"', '\\"');
    return `repo:${owner}/${repo} is:issue label:failing-test in:body "${escapedMarker}"`;
}

function isSupportedSourceUrl(value) {
    if (typeof value !== 'string') {
        return false;
    }

    return /^https:\/\/github\.com\/[^/]+\/[^/]+\/pull\/\d+(?:\/.*)?$/i.test(value)
        || /^https:\/\/github\.com\/[^/]+\/[^/]+\/actions\/runs\/\d+(?:\/attempts\/\d+)?(?:\/.*)?$/i.test(value)
        || /^https:\/\/github\.com\/[^/]+\/[^/]+\/actions\/runs\/\d+\/job\/\d+(?:\/.*)?$/i.test(value);
}

module.exports = {
    buildIssueSearchQuery,
    formatListResponse,
    isSupportedSourceUrl,
    parseCommand,
};
