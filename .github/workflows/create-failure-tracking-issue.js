// Shared helper for "open or comment on a failure tracking issue" used by
// workflows that auto-create issues when they fail (e.g.
// validate-published-build.yml). Callers compute their own title, body, and
// labels — the helper only owns the search → exact-title-match →
// comment-or-create flow.
//
// Loaded from workflows via:
//   const helper = require(`${process.env.GITHUB_WORKSPACE}/.github/workflows/create-failure-tracking-issue.js`);

'use strict';

/**
 * Builds a GitHub Search API query that matches open issues whose title
 * contains the given phrase. Callers MUST post-filter with title === title
 * because GitHub's search tokenizes punctuation (e.g. "13.4" can match
 * "13.4.1" at the index level).
 */
function buildDedupQuery(owner, repo, title) {
    if (!owner || !repo || !title) {
        throw new Error('buildDedupQuery requires owner, repo, and title.');
    }

    return `repo:${owner}/${repo} is:issue is:open in:title ${JSON.stringify(title)}`;
}

/**
 * Open a new tracking issue or, if one with the same title is already open,
 * comment on it with the run URL.
 *
 * Returns { action: 'commented' | 'created', issueUrl, issueNumber }.
 */
async function createOrCommentOnFailureIssue({
    github,
    core,
    owner,
    repo,
    title,
    body,
    labels,
    runUrl,
}) {
    if (!github || !owner || !repo || !title || !body || !runUrl) {
        throw new Error('createOrCommentOnFailureIssue requires github, owner, repo, title, body, and runUrl.');
    }

    const labelsArray = Array.isArray(labels) ? labels : [];

    let existing = null;
    try {
        const q = buildDedupQuery(owner, repo, title);
        const search = await github.rest.search.issuesAndPullRequests({ q, per_page: 10 });
        existing = (search.data.items ?? []).find(item => item.title === title) ?? null;
    }
    catch (error) {
        core?.warning?.(`Could not search for existing tracking issues; creating a new one to avoid losing the report. ${error.message ?? error}`);
    }

    if (existing) {
        await github.rest.issues.createComment({
            owner,
            repo,
            issue_number: existing.number,
            body: `Another failure occurred. See: ${runUrl}`,
        });
        core?.info?.(`Commented on existing issue: ${existing.html_url}`);
        return { action: 'commented', issueUrl: existing.html_url, issueNumber: existing.number };
    }

    const created = await github.rest.issues.create({
        owner,
        repo,
        title,
        body,
        labels: labelsArray,
    });
    core?.info?.(`Created issue: ${created.data.html_url}`);
    return { action: 'created', issueUrl: created.data.html_url, issueNumber: created.data.number };
}

module.exports = {
    buildDedupQuery,
    createOrCommentOnFailureIssue,
};
