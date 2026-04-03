function parseCommand(body, defaultSourceUrl = null) {
    const match = /^\/create-issue(?:\s+(?<args>.+))?$/m.exec(body ?? '');
    if (!match) {
        return { success: false, errorMessage: 'No /create-issue command was found in the comment.' };
    }

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
            return { success: false, errorMessage: 'The flag-based form requires --test "<test-name>".' };
        }

        return result;
    }

    if (tokens.length === 0) {
        return { success: false, errorMessage: 'The command requires a test name. Use /create-issue --test "<test-name>".' };
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

function tokenizeArguments(input) {
    const tokens = [];
    let current = '';
    let quote = null;

    for (let index = 0; index < input.length; index++) {
        const character = input[index];

        if (quote) {
            if (character === '\\' && index + 1 < input.length) {
                const next = input[index + 1];
                if (next === quote || next === '\\') {
                    current += next;
                    index++;
                    continue;
                }
            }

            if (character === quote) {
                quote = null;
                continue;
            }

            current += character;
            continue;
        }

        if (character === '"' || character === '\'') {
            quote = character;
            continue;
        }

        if (/\s/.test(character)) {
            if (current.length > 0) {
                tokens.push(current);
                current = '';
            }

            continue;
        }

        current += character;
    }

    if (quote) {
        throw new Error(`Unterminated ${quote} quote in command arguments.`);
    }

    if (current.length > 0) {
        tokens.push(current);
    }

    return tokens;
}

module.exports = {
    buildIssueSearchQuery,
    isSupportedSourceUrl,
    parseCommand,
};
