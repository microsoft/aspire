module.exports = async function updateCopilotAssigneeLabel({ github, context }) {
    const label = 'needs-assignee';
    const copilotAuthors = new Set([
        'copilot',
        'copilot[bot]',
        'copilot-swe-agent',
        'copilot-swe-agent[bot]',
        'github-copilot[bot]',
    ]);
    const { owner, repo } = context.repo;
    const number = context.payload.pull_request.number;
    const { data: pullRequest } = await github.rest.pulls.get({
        owner,
        repo,
        pull_number: number,
    });

    if (!copilotAuthors.has(pullRequest.user?.login?.toLowerCase())) {
        return;
    }

    const hasHumanAssignee = pullRequest.assignees.some((assignee) => assignee.type !== 'Bot');
    const hasLabel = pullRequest.labels.some((existing) => existing.name.toLowerCase() === label);
    const needsAssignee = pullRequest.state === 'open' && !hasHumanAssignee;

    if (needsAssignee && !hasLabel) {
        // The label is pre-created as repository configuration so label mutations do not
        // require broader issues: write permission.
        await github.rest.issues.addLabels({
            owner,
            repo,
            issue_number: number,
            labels: [label],
        });
    } else if (!needsAssignee && hasLabel) {
        try {
            await github.rest.issues.removeLabel({
                owner,
                repo,
                issue_number: number,
                name: label,
            });
        } catch (error) {
            // An assignment or manual label update can remove the label after the refetch.
            if (error.status !== 404) {
                throw error;
            }
        }
    }
};
