/**
 * Shared helpers for GitHub Actions workflow command parsing.
 *
 * Used by:
 *   - create-failing-test-issue.js
 *   - apply-test-attributes.yml (inline github-script)
 *
 * Tested via:
 *   - tests/Infrastructure.Tests/WorkflowScripts/CreateFailingTestIssueWorkflowTests.cs
 */

/**
 * Tokenizes a command argument string, respecting single and double quotes
 * with backslash escaping inside quoted segments.
 *
 * @param {string} input - Raw argument string (e.g., '--test "My Test" --url https://...')
 * @returns {string[]} Array of tokens
 * @throws {Error} If a quoted segment is not closed
 */
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
    tokenizeArguments,
};
