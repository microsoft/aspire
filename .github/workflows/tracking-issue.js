// Generic deterministic lifecycle helpers for automated tracking issues.
//
// The pure planner owns identity lookup, canonical selection, and the ordered
// mutation plan. The executor applies that plan through an injected transport,
// so live Octokit callers, dry-run renderers, and future CLI-backed callers use
// the same lifecycle decisions.

'use strict';

const DUPLICATE_EXEMPT_MARKER = '<!-- tracking-issue-duplicate-exempt -->';

function findIssuesForMarkers(issues, markers, isMatchingIssue = () => true) {
    const exactMarkers = [...new Set(
        (markers ?? []).filter(marker => typeof marker === 'string' && marker.length > 0))];
    return (issues ?? [])
        .filter(issue =>
            typeof issue?.body === 'string' &&
            exactMarkers.some(marker => issue.body.includes(marker)) &&
            isMatchingIssue(issue))
        .sort((left, right) => left.number - right.number);
}

function findIssueForMarker(issues, marker) {
    return findIssuesForMarkers(issues, [marker])[0] ?? null;
}

function findOpenIssueForMarker(issues, marker) {
    return findIssueForMarker(
        (issues ?? []).filter(issue => issue?.state === undefined || issue.state === 'open'),
        marker);
}

function runMarker(runId) {
    return `<!-- run:${runId} -->`;
}

function duplicateExemptStamp() {
    return DUPLICATE_EXEMPT_MARKER;
}

function isDuplicateExempt(issue) {
    return typeof issue?.body === 'string' &&
        issue.body.replaceAll('\r\n', '\n').trimEnd().split('\n').at(-1) === DUPLICATE_EXEMPT_MARKER;
}

function buildBody({ marker, lead, note, autoClose }) {
    const head = autoClose === undefined ? [marker] : [marker, autoCloseStamp(autoClose)];
    return [
        ...head,
        '',
        lead,
        '',
        ...note,
        '',
    ].join('\n');
}

function autoCloseStamp(autoClose) {
    return `<!-- autoclose:${autoClose ? 'true' : 'false'} -->`;
}

function readAutoClose(body) {
    if (typeof body !== 'string') {
        return null;
    }

    const match = /<!--\s*autoclose:(true|false)\s*-->/i.exec(body);
    if (match === null) {
        return null;
    }

    return match[1].toLowerCase() === 'true';
}

function duplicateReconciliationMarker(canonicalIssueNumber, duplicateIssueNumber) {
    return `<!-- tracking-issue-duplicate:v1:${canonicalIssueNumber}:${duplicateIssueNumber} -->`;
}

function hasMarker(items, marker) {
    if (!Array.isArray(items)) {
        return false;
    }

    return items.some(
        item => typeof (item?.body ?? item) === 'string' && (item.body ?? item).includes(marker));
}

function normalizeCanonicalActions(actions, issueNumber) {
    return (actions ?? []).map(action => ({
        ...action,
        issueNumber: action.issueNumber ?? issueNumber,
    }));
}

// Returns an ordered, side-effect-free mutation plan. Identity callbacks let
// producers validate marker metadata and prefer the canonical marker over
// historical aliases.
function planIssueReconciliation({
    issues,
    label,
    labels,
    marker,
    alternateMarkers = [],
    title,
    buildBody,
    closeDuplicates = false,
    createIfMissing = true,
    forceCreate = false,
    isMatchingIssue = () => true,
    isCanonicalIssue = () => false,
    reopen = 'when-changing',
    actionsForCanonical = () => [],
    canReconcileDuplicates = () => true,
}) {
    if (forceCreate) {
        const body = buildBody();
        return {
            canonicalIssueNumber: null,
            created: true,
            requiresRelist: false,
            matches: [],
            actions: [{ type: 'create', title, body, labels: [...new Set([label, ...(labels ?? [])])] }],
        };
    }

    const matches = findIssuesForMarkers(
        issues,
        [marker, ...alternateMarkers],
        isMatchingIssue)
        .filter(issue => !isDuplicateExempt(issue));
    const issueLabels = [...new Set([label, ...(labels ?? [])])];

    if (matches.length === 0) {
        if (!createIfMissing) {
            return {
                canonicalIssueNumber: null,
                created: false,
                requiresRelist: false,
                matches: [],
                actions: [],
            };
        }

        const body = buildBody();
        const syntheticIssue = {
            number: null,
            state: 'open',
            title,
            body,
            labels: issueLabels,
            comments: [],
        };
        const canonicalActions = normalizeCanonicalActions(
            actionsForCanonical(syntheticIssue, { created: true, matches: [syntheticIssue] }),
            null);
        return {
            canonicalIssueNumber: null,
            created: true,
            requiresRelist: true,
            matches: [],
            actions: [
                { type: 'create', title, body, labels: issueLabels },
                ...canonicalActions,
            ],
        };
    }

    const canonical = matches.find(isCanonicalIssue) ?? matches[0];
    const actions = [];
    const canonicalActions = normalizeCanonicalActions(
        actionsForCanonical(canonical, { created: false, matches }),
        canonical.number);
    const hasExplicitReopen = canonicalActions.some(action => action.type === 'reopen');
    const shouldReopen =
        canonical.state === 'closed' &&
        !hasExplicitReopen &&
        (reopen === 'always' || (reopen === 'when-changing' && canonicalActions.length > 0));
    if (shouldReopen) {
        actions.push({ type: 'reopen', issueNumber: canonical.number });
    }
    actions.push(...canonicalActions);

    if (closeDuplicates && canReconcileDuplicates(canonical, { matches, canonicalActions })) {
        for (const duplicate of matches.filter(issue => issue.number !== canonical.number)) {
            if (duplicate.state === 'closed') {
                continue;
            }

            const reconciliationMarker = duplicateReconciliationMarker(
                canonical.number,
                duplicate.number);
            if (!hasMarker(canonical.comments, reconciliationMarker)) {
                actions.push({
                    type: 'comment',
                    issueNumber: canonical.number,
                    body: `[automated] Issue #${duplicate.number} was identified as a duplicate.\n\n${reconciliationMarker}`,
                });
            }
            if (!hasMarker(duplicate.comments, reconciliationMarker)) {
                actions.push({
                    type: 'comment',
                    issueNumber: duplicate.number,
                    body: `[automated] Duplicate of #${canonical.number}. Future occurrences are tracked there.\n\n${reconciliationMarker}`,
                });
            }
            actions.push({
                type: 'close',
                issueNumber: duplicate.number,
                stateReason: 'not_planned',
            });
        }
    }

    return {
        canonicalIssueNumber: canonical.number,
        created: false,
        requiresRelist: false,
        matches,
        actions,
    };
}

async function executeAction(transport, action, canonicalIssueNumber) {
    const issueNumber = action.issueNumber ?? canonicalIssueNumber;
    switch (action.type) {
        case 'update':
            await transport.updateIssue(issueNumber, {
                ...(action.body === undefined ? {} : { body: action.body }),
                ...(action.title === undefined ? {} : { title: action.title }),
            });
            break;
        case 'comment':
            await transport.addComment(issueNumber, action.body);
            break;
        case 'close':
            await transport.closeIssue(issueNumber, { stateReason: action.stateReason });
            break;
        case 'reopen':
            await transport.reopenIssue(issueNumber);
            break;
        default:
            throw new Error(`Unsupported issue reconciliation action '${action.type}'.`);
    }

    return { ...action, issueNumber };
}

async function preparePlanningIssues(issues, prepareIssues, transport, options) {
    let preparedIssues = prepareIssues ? await prepareIssues(issues) : issues;
    if (!options.closeDuplicates || typeof transport.listComments !== 'function') {
        return preparedIssues;
    }

    const matches = findIssuesForMarkers(
        preparedIssues,
        [options.marker, ...(options.alternateMarkers ?? [])],
        options.isMatchingIssue ?? (() => true))
        .filter(issue => !isDuplicateExempt(issue))
        .filter(issue => !Array.isArray(issue.comments));
    const commentsByNumber = new Map();
    await Promise.all(matches.map(async issue => {
        commentsByNumber.set(issue.number, await transport.listComments(issue.number));
    }));
    preparedIssues = preparedIssues.map(issue => ({
        ...issue,
        comments: commentsByNumber.get(issue.number) ?? issue.comments,
    }));
    return preparedIssues;
}

async function executeIssueReconciliation(transport, core, options) {
    let issues = options.issues ?? await transport.listIssues(options.label);
    issues = await preparePlanningIssues(issues, options.prepareIssues, transport, options);
    let plan = planIssueReconciliation({ ...options, issues });
    const appliedActions = [];
    let createdIssue = null;

    if (plan.actions[0]?.type === 'create') {
        const [createAction, ...pendingActions] = plan.actions;
        createdIssue = await transport.createIssue({
            title: createAction.title,
            body: createAction.body,
            labels: createAction.labels,
        });
        appliedActions.push({ ...createAction, issueNumber: createdIssue.number });

        if (plan.requiresRelist) {
            issues = await transport.listIssues(options.label);
            issues = await preparePlanningIssues(issues, options.prepareIssues, transport, options);
            plan = planIssueReconciliation({ ...options, issues });
            if (plan.actions[0]?.type === 'create') {
                throw new Error('Created tracking issue was not returned by the all-state label listing.');
            }
            for (const action of plan.actions) {
                appliedActions.push(await executeAction(
                    transport,
                    action,
                    plan.canonicalIssueNumber));
            }
        } else {
            plan = {
                ...plan,
                canonicalIssueNumber: createdIssue.number,
                requiresRelist: false,
                actions: pendingActions,
            };
            for (const action of pendingActions) {
                appliedActions.push(await executeAction(
                    transport,
                    action,
                    createdIssue.number));
            }
        }
    } else {
        for (const action of plan.actions) {
            appliedActions.push(await executeAction(
                transport,
                action,
                plan.canonicalIssueNumber));
        }
    }

    const canonicalIssueNumber = plan.canonicalIssueNumber ?? createdIssue?.number ?? null;
    if (canonicalIssueNumber === null) {
        core.info('No matching tracking issue to reconcile.');
    } else {
        core.info(`Reconciled tracking issue #${canonicalIssueNumber}.`);
    }
    return {
        number: canonicalIssueNumber,
        created: createdIssue?.number === canonicalIssueNumber,
        reopened: appliedActions.some(action =>
            action.type === 'reopen' && action.issueNumber === canonicalIssueNumber),
        duplicatesClosed: appliedActions
            .filter(action =>
                action.type === 'close' && action.issueNumber !== canonicalIssueNumber)
            .map(action => action.issueNumber),
        appliedActions,
        plan,
    };
}

async function ensureLabel(github, owner, repo, { name, color, description }) {
    try {
        await github.rest.issues.createLabel({ owner, repo, name, color, description });
    } catch (error) {
        if (error.status !== 422) {
            throw error;
        }
    }
}

async function listIssuesByLabel(github, owner, repo, label, { state = 'all' } = {}) {
    const items = await github.paginate(github.rest.issues.listForRepo, {
        owner, repo, labels: label, state, per_page: 100,
    });
    return items.filter(item => !item.pull_request);
}

async function listOpenIssuesByLabel(github, owner, repo, label) {
    return await listIssuesByLabel(github, owner, repo, label, { state: 'open' });
}

async function createIssue(github, owner, repo, { title, body, labels }) {
    const created = await github.rest.issues.create({ owner, repo, title, body, labels });
    return created.data;
}

async function updateIssue(github, owner, repo, issueNumber, patch) {
    const updated = await github.rest.issues.update({
        owner,
        repo,
        issue_number: issueNumber,
        ...patch,
    });
    return updated?.data;
}

async function addComment(github, owner, repo, issueNumber, body) {
    await github.rest.issues.createComment({ owner, repo, issue_number: issueNumber, body });
}

async function closeIssue(github, owner, repo, issueNumber, { stateReason = 'completed' } = {}) {
    await updateIssue(github, owner, repo, issueNumber, {
        state: 'closed',
        state_reason: stateReason,
    });
}

async function reopenIssue(github, owner, repo, issueNumber) {
    await updateIssue(github, owner, repo, issueNumber, { state: 'open' });
}

async function listComments(github, owner, repo, issueNumber) {
    return await github.paginate(github.rest.issues.listComments, {
        owner, repo, issue_number: issueNumber, per_page: 100,
    });
}

async function hasCommentForRun(github, owner, repo, issueNumber, marker) {
    return hasMarker(await listComments(github, owner, repo, issueNumber), marker);
}

function createOctokitIssueTransport(github, context) {
    const { owner, repo } = context.repo;
    return {
        ensureLabel: definition => ensureLabel(github, owner, repo, definition),
        listIssues: label => listIssuesByLabel(github, owner, repo, label),
        createIssue: request => createIssue(github, owner, repo, request),
        updateIssue: (issueNumber, patch) => updateIssue(
            github, owner, repo, issueNumber, patch),
        addComment: (issueNumber, body) => addComment(
            github, owner, repo, issueNumber, body),
        closeIssue: (issueNumber, options) => closeIssue(
            github, owner, repo, issueNumber, options),
        reopenIssue: issueNumber => reopenIssue(github, owner, repo, issueNumber),
        listComments: issueNumber => listComments(github, owner, repo, issueNumber),
    };
}

function createDryRunIssueTransport(sourceTransport, issues, onAction = () => {}) {
    const inventory = (issues ?? []).map(issue => ({
        ...issue,
        labels: [...(issue.labels ?? [])],
        comments: Array.isArray(issue.comments) ? [...issue.comments] : undefined,
    }));
    let nextSyntheticIssueNumber = 0;

    const findIssue = issueNumber => inventory.find(issue => issue.number === issueNumber);
    return {
        ensureLabel: async definition => {
            onAction({ type: 'ensure-label', ...definition });
        },
        listIssues: async () => inventory,
        createIssue: async request => {
            const issue = {
                number: nextSyntheticIssueNumber--,
                state: 'open',
                comments: [],
                ...request,
                dryRunPlaceholder: true,
            };
            inventory.push(issue);
            onAction({ type: 'create', issueNumber: issue.number, ...request });
            return issue;
        },
        updateIssue: async (issueNumber, patch) => {
            Object.assign(findIssue(issueNumber), patch);
            onAction({ type: 'update', issueNumber, ...patch });
        },
        addComment: async (issueNumber, body) => {
            const issue = findIssue(issueNumber);
            (issue.comments ??= []).push(body);
            onAction({ type: 'comment', issueNumber, body });
        },
        closeIssue: async (issueNumber, { stateReason = 'completed' } = {}) => {
            const issue = findIssue(issueNumber);
            issue.state = 'closed';
            issue.state_reason = stateReason;
            onAction({ type: 'close', issueNumber, stateReason });
        },
        reopenIssue: async issueNumber => {
            findIssue(issueNumber).state = 'open';
            onAction({ type: 'reopen', issueNumber });
        },
        listComments: async issueNumber => {
            const issue = findIssue(issueNumber);
            if (issue.comments === undefined) {
                issue.comments = await sourceTransport.listComments(issueNumber);
            }
            return issue.comments;
        },
        issues: inventory,
    };
}

async function reconcileRun(
    transport,
    core,
    {
        label,
        labels,
        marker,
        title,
        runId,
        buildBody: buildIssueBody,
        comment,
        issues,
        alternateMarkers = [],
        closeDuplicates = false,
        isMatchingIssue = () => true,
    }) {
    const occurrenceMarker = runMarker(runId);
    const commentBody = `${comment}\n\n${occurrenceMarker}`;
    const prepareIssues = async inventory => {
        const matches = findIssuesForMarkers(
            inventory,
            [marker, ...alternateMarkers],
            isMatchingIssue)
            .filter(issue => !isDuplicateExempt(issue));
        const openMatches = matches.filter(issue => issue.state !== 'closed');
        const planningMatches = !closeDuplicates && openMatches.length > 0
            ? openMatches
            : matches;
        const allMatchNumbers = new Set(matches.map(issue => issue.number));
        const planningMatchNumbers = new Set(planningMatches.map(issue => issue.number));
        const commentsByNumber = new Map();
        await Promise.all(planningMatches.map(async issue => {
            commentsByNumber.set(issue.number, await transport.listComments(issue.number));
        }));
        return inventory
            .filter(issue =>
                !allMatchNumbers.has(issue.number) || planningMatchNumbers.has(issue.number))
            .map(issue => ({
                ...issue,
                comments: commentsByNumber.get(issue.number) ?? issue.comments,
            }));
    };
    const result = await executeIssueReconciliation(transport, core, {
        label,
        labels,
        marker,
        alternateMarkers,
        title,
        buildBody: buildIssueBody,
        issues,
        closeDuplicates,
        isMatchingIssue,
        reopen: 'when-changing',
        prepareIssues,
        actionsForCanonical: (issue, { matches }) => {
            const occurrenceIssue = matches.find(
                match => hasMarker(match.comments, occurrenceMarker));
            if (occurrenceIssue) {
                if (issue.state === 'closed' && occurrenceIssue.number !== issue.number) {
                    return [{ type: 'reopen' }];
                }
                return [];
            }
            return [{ type: 'comment', body: commentBody }];
        },
    });
    const recorded = result.appliedActions.some(
        action => action.type === 'comment' && action.body === commentBody);
    if (!recorded) {
        core.info(`Run ${runId} already recorded in #${result.number}; skipping duplicate comment.`);
    }

    return {
        number: result.number,
        created: result.created,
        skipped: !recorded,
        reopened: result.reopened,
        duplicatesClosed: result.duplicatesClosed,
        appliedActions: result.appliedActions,
        plan: result.plan,
    };
}

async function recordRun(github, context, core, options) {
    return await reconcileRun(createOctokitIssueTransport(github, context), core, options);
}

module.exports = {
    findIssuesForMarkers,
    findIssueForMarker,
    findOpenIssueForMarker,
    runMarker,
    duplicateExemptStamp,
    isDuplicateExempt,
    buildBody,
    autoCloseStamp,
    readAutoClose,
    planIssueReconciliation,
    executeIssueReconciliation,
    createOctokitIssueTransport,
    createDryRunIssueTransport,
    ensureLabel,
    listIssuesByLabel,
    listOpenIssuesByLabel,
    createIssue,
    updateIssue,
    addComment,
    closeIssue,
    reopenIssue,
    hasCommentForRun,
    reconcileRun,
    recordRun,
};
