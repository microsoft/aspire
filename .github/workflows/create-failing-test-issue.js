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
                    return {
                        success: false,
                        errorMessage: `Unknown argument '${token}'. Supported arguments are --test, --url, --workflow, and --force-new.`,
                    };
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
    isSupportedSourceUrl,
    parseCommand,
};
