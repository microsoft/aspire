const fs = require('node:fs');
const path = require('node:path');

const trackedClassifications = new Set(['flaky-test', 'transient-infra']);
const supportedCauseTypes = new Set(['flaky-test', 'infra-failure']);
const safeCauseIdPattern = /^[a-z0-9][a-z0-9-]*$/;

function resolveCauses({ analysis, causes, priorCauses = [], retryPatterns = {} }) {
    validateInputs(analysis, causes, priorCauses);

    priorCauses = priorCauses.filter(
        cause => cause && typeof cause.id === 'string' && cause.id.length > 0);
    const proposalNormalization = normalizeProposedCauseIds(analysis, causes);
    analysis = proposalNormalization.analysis;
    causes = proposalNormalization.causes;

    const priorById = new Map(priorCauses.map(cause => [cause.id, cause]));
    // Historical memory predates the slug contract. Match sanitized proposals back to those
    // records so fixing an ID does not split one cause into old and new identities.
    const priorByNormalizedId = buildPriorByNormalizedId(priorCauses);
    const failedJobsById = new Map(analysis.failed_jobs.map(job => [job.id, job]));
    const canonicalizations = [...proposalNormalization.canonicalizations];
    const normalizedById = new Map();
    const proposedToCanonical = new Map();
    const priorCauseMigrations = new Map();

    for (const cause of causes) {
        const jobIds = resolveCauseJobIds(cause, analysis, failedJobsById);
        const jobNames = jobIds.map(jobId => failedJobsById.get(jobId).name);
        const evidence = buildEvidence(cause, analysis, jobIds);

        const proposedAlias = findPriorCauseByProposedId(cause, priorById, priorByNormalizedId);
        let testNameMatch;
        let retryPatternMatch;
        let explicitMatcherMatch;

        if (!proposedAlias) {
            testNameMatch = findPriorCauseByTestName(cause, priorCauses, priorById);
            retryPatternMatch = findPriorCauseByRetryPattern(evidence, jobNames, retryPatterns, priorById);
            explicitMatcherMatch = findPriorCauseByExplicitMatcher(evidence, priorCauses, priorById);
            const crossMechanismMatches = uniqueById(
                [testNameMatch, retryPatternMatch, explicitMatcherMatch].filter(Boolean));

            if (crossMechanismMatches.length > 1) {
                throw new Error(
                    `Failure matched conflicting canonical prior causes: ${crossMechanismMatches.map(match => match.id).join(', ')}.`);
            }
        }

        // An explicit alias is authoritative. Otherwise normalized test identity is the primary
        // key for flaky tests, while retry patterns and matchers cover cross-test root causes.
        const canonicalPriorCause =
            proposedAlias ??
            testNameMatch ??
            retryPatternMatch ??
            explicitMatcherMatch ??
            findPriorCauseByExistingId(cause, priorById, priorByNormalizedId);

        const priorCauseId = canonicalPriorCause?.id;
        const canonicalId = getCanonicalCauseId(cause.id, priorCauseId);
        const normalizedCause = normalizeCause(cause, canonicalPriorCause, canonicalId, jobIds, jobNames);
        validateCauseType(normalizedCause);
        proposedToCanonical.set(cause.id, canonicalId);

        if (priorCauseId && priorCauseId !== canonicalId) {
            priorCauseMigrations.set(priorCauseId, canonicalId);
        }

        if (cause.id !== canonicalId) {
            canonicalizations.push({ proposed_id: cause.id, canonical_id: canonicalId });
        }

        const existing = normalizedById.get(canonicalId);
        normalizedById.set(
            canonicalId,
            existing ? mergeCurrentCauses(existing, normalizedCause) : normalizedCause);
    }

    const normalizedCauses = [...normalizedById.values()];
    const referencedCauseIds = analysis.causes.map(causeId => {
        const canonicalId = proposedToCanonical.get(causeId);
        if (!canonicalId) {
            throw new Error(`Run summary references cause '${causeId}', but no matching cause file was produced.`);
        }

        return canonicalId;
    });

    const normalizedAnalysis = {
        ...analysis,
        causes: unique([...referencedCauseIds, ...normalizedCauses.map(cause => cause.id)]),
        failed_jobs: analysis.failed_jobs.map(job => ({
            ...job,
            cause_ids: normalizedCauses
                .filter(cause => cause.job_ids.includes(job.id))
                .map(cause => cause.id),
        })),
        failed_tests: analysis.failed_tests.map(test => {
            const cause = normalizedCauses.find(candidate =>
                candidate.test_names?.some(name => normalizeTestName(name) === normalizeTestName(test.name)) &&
                candidate.job_names.includes(test.job));

            return cause ? { ...test, cause_id: cause.id } : test;
        }),
    };

    validateTrackedJobsHaveCauses(normalizedAnalysis);

    return {
        analysis: normalizedAnalysis,
        causes: normalizedCauses,
        canonicalizations,
        priorCauseMigrations: [...priorCauseMigrations].map(([legacyId, canonicalId]) => ({
            legacy_id: legacyId,
            canonical_id: canonicalId,
        })),
    };
}

function validateInputs(analysis, causes, priorCauses) {
    if (!analysis || !Array.isArray(analysis.failed_jobs) || !Array.isArray(analysis.failed_tests) || !Array.isArray(analysis.causes)) {
        throw new Error('Analysis must contain failed_jobs, failed_tests, and causes arrays.');
    }

    if (!Array.isArray(causes) || !Array.isArray(priorCauses)) {
        throw new Error('Causes and priorCauses must be arrays.');
    }

    for (const causeId of analysis.causes) {
        if (typeof causeId !== 'string' || causeId.length === 0) {
            throw new Error(`Invalid cause ID '${causeId ?? ''}'.`);
        }
    }

    for (const cause of causes) {
        if (!cause || typeof cause.id !== 'string' || cause.id.length === 0) {
            throw new Error(`Invalid cause ID '${cause?.id ?? ''}'.`);
        }
        validateCauseType(cause);
    }
}

function validateCauseType(cause) {
    if (!supportedCauseTypes.has(cause.type)) {
        throw new Error(`Cause '${cause.id}' has unsupported type '${cause.type ?? ''}'.`);
    }
}

function normalizeProposedCauseIds(analysis, causes) {
    const normalizedByProposed = new Map();
    const proposedByNormalized = new Map();
    const canonicalizations = [];

    for (const cause of causes) {
        const normalizedId = sanitizeProposedCauseId(cause.id);
        const existingProposedId = proposedByNormalized.get(normalizedId);
        if (existingProposedId && existingProposedId !== cause.id) {
            throw new Error(
                `Cause IDs '${existingProposedId}' and '${cause.id}' normalize to the same cause ID '${normalizedId}'.`);
        }

        normalizedByProposed.set(cause.id, normalizedId);
        proposedByNormalized.set(normalizedId, cause.id);
        if (cause.id !== normalizedId) {
            canonicalizations.push({ proposed_id: cause.id, canonical_id: normalizedId });
        }
    }

    return {
        analysis: {
            ...analysis,
            causes: analysis.causes.map(causeId => normalizedByProposed.get(causeId) ?? causeId),
        },
        causes: causes.map(cause => ({
            ...cause,
            id: normalizedByProposed.get(cause.id),
        })),
        canonicalizations,
    };
}

function buildPriorByNormalizedId(priorCauses) {
    const priorByNormalizedId = new Map();

    for (const cause of priorCauses) {
        const normalizedId = normalizeCauseId(cause.id);
        if (!safeCauseIdPattern.test(normalizedId)) {
            continue;
        }

        if (!priorByNormalizedId.has(normalizedId)) {
            priorByNormalizedId.set(normalizedId, cause);
        } else if (priorByNormalizedId.get(normalizedId)?.id !== cause.id) {
            priorByNormalizedId.set(normalizedId, null);
        }
    }

    return priorByNormalizedId;
}

function sanitizeProposedCauseId(causeId) {
    const normalizedId = normalizeCauseId(causeId);
    if (!safeCauseIdPattern.test(normalizedId)) {
        throw new Error(`Invalid cause ID '${causeId}'.`);
    }

    return normalizedId;
}

function normalizeCauseId(causeId) {
    return String(causeId ?? '')
        .trim()
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '');
}

function getCanonicalCauseId(proposedCauseId, priorCauseId) {
    if (!priorCauseId) {
        return proposedCauseId;
    }
    if (safeCauseIdPattern.test(priorCauseId)) {
        return priorCauseId;
    }

    const normalizedPriorCauseId = normalizeCauseId(priorCauseId);
    return safeCauseIdPattern.test(normalizedPriorCauseId) ? normalizedPriorCauseId : proposedCauseId;
}

function resolveCauseJobIds(cause, analysis, failedJobsById) {
    let jobIds = cause.job_ids;

    if (!Array.isArray(jobIds) || jobIds.length === 0) {
        throw new Error(`Cause '${cause.id}' must reference at least one failed job.`);
    }

    jobIds = unique(jobIds);
    for (const jobId of jobIds) {
        const failedJob = failedJobsById.get(jobId);
        if (!failedJob) {
            throw new Error(`Cause '${cause.id}' references unknown failed job ID '${jobId}'.`);
        }
        if (!trackedClassifications.has(failedJob.classification)) {
            throw new Error(
                `Cause '${cause.id}' references job '${failedJob.name}', which is classified as '${failedJob.classification}'.`);
        }
    }

    if (cause.test_name) {
        const normalizedTestName = normalizeTestName(cause.test_name);
        const missingJobIds = jobIds.filter(jobId => {
            const jobName = failedJobsById.get(jobId).name;
            return !analysis.failed_tests.some(test =>
                test.job === jobName && normalizeTestName(test.name) === normalizedTestName);
        });
        if (missingJobIds.length > 0) {
            throw new Error(
                `Cause '${cause.id}' names test '${cause.test_name}', but that test is not in its referenced failed jobs.`);
        }
    }

    return jobIds;
}

function buildEvidence(cause, analysis, jobIds) {
    const jobIdSet = new Set(jobIds);
    const jobNames = new Set(analysis.failed_jobs.filter(job => jobIdSet.has(job.id)).map(job => job.name));
    const causeTestNames = new Set(cause.test_name ? [normalizeTestName(cause.test_name)] : []);
    const failedTests = analysis.failed_tests.filter(test =>
        jobNames.has(test.job) && causeTestNames.has(normalizeTestName(test.name)));

    return [
        cause.title,
        cause.error_pattern,
        ...failedTests.flatMap(test => [test.name, test.error, test.stack_trace]),
    ].filter(value => typeof value === 'string' && value.length > 0).join('\n');
}

function findPriorCauseByProposedId(cause, priorById, priorByNormalizedId) {
    const priorCause = findPriorCauseById(cause.id, priorById, priorByNormalizedId);
    return priorCause?.canonical_id ? resolveAlias(priorCause, priorById) : undefined;
}

function findPriorCauseByExistingId(cause, priorById, priorByNormalizedId) {
    const priorCause = findPriorCauseById(cause.id, priorById, priorByNormalizedId);
    return priorCause ? resolveAlias(priorCause, priorById) : undefined;
}

function findPriorCauseById(causeId, priorById, priorByNormalizedId) {
    return priorById.get(causeId) ?? priorByNormalizedId.get(causeId);
}

function findPriorCauseByTestName(cause, priorCauses, priorById) {
    if (!cause.test_name) {
        return undefined;
    }

    const normalizedTestName = normalizeTestName(cause.test_name);
    const candidates = priorCauses.filter(prior =>
        allTestNames(prior).some(testName => normalizeTestName(testName) === normalizedTestName));

    return selectOldestCanonicalCause(candidates, priorById);
}

function findPriorCauseByRetryPattern(evidence, jobNames, retryPatterns, priorById) {
    const matchingCauseIds = unique((retryPatterns.jobFailurePatterns ?? [])
        .filter(pattern => pattern.enabled !== false)
        .filter(pattern => pattern.causeId)
        .filter(pattern => matchesConfiguredPattern(pattern.output, evidence))
        .filter(pattern => !pattern.jobName || jobNames.some(jobName => matchesConfiguredPattern(pattern.jobName, jobName)))
        .map(pattern => pattern.causeId));

    if (matchingCauseIds.length > 1) {
        throw new Error(`Failure matched multiple retry-pattern cause IDs: ${matchingCauseIds.join(', ')}.`);
    }

    if (matchingCauseIds.length === 0) {
        return undefined;
    }

    const causeId = matchingCauseIds[0];
    const priorCause = priorById.get(causeId);
    return priorCause ? resolveAlias(priorCause, priorById) : { id: causeId };
}

function findPriorCauseByExplicitMatcher(evidence, priorCauses, priorById) {
    const candidates = [];

    for (const priorCause of priorCauses) {
        if ((priorCause.matchers ?? []).some(matcher => matchesExplicitMatcher(matcher, evidence))) {
            candidates.push(resolveAlias(priorCause, priorById));
        }
    }

    const canonicalCandidates = uniqueById(candidates);
    if (canonicalCandidates.length > 1) {
        throw new Error(
            `Failure matched multiple canonical prior causes: ${canonicalCandidates.map(cause => cause.id).join(', ')}.`);
    }

    return canonicalCandidates[0];
}

function selectOldestCanonicalCause(candidates, priorById) {
    const canonicalCandidates = uniqueById(candidates.map(cause => resolveAlias(cause, priorById)));
    return canonicalCandidates.sort((left, right) => {
        const dateComparison = firstObservedAt(left).localeCompare(firstObservedAt(right));
        return dateComparison !== 0 ? dateComparison : left.id.localeCompare(right.id);
    })[0];
}

function resolveAlias(cause, priorById) {
    const visited = new Set();
    let current = cause;

    while (current.canonical_id) {
        if (visited.has(current.id)) {
            throw new Error(`Cause alias cycle detected at '${current.id}'.`);
        }

        visited.add(current.id);
        const canonical = priorById.get(current.canonical_id);
        if (!canonical) {
            throw new Error(`Cause '${current.id}' aliases missing canonical cause '${current.canonical_id}'.`);
        }

        current = canonical;
    }

    return current;
}

function normalizeCause(cause, priorCause, canonicalId, jobIds, jobNames) {
    const testNames = unique([
        ...allTestNames(priorCause ?? {}),
        cause.test_name,
    ].filter(Boolean));

    return removeUndefined({
        ...cause,
        id: canonicalId,
        // Alias metadata is owned by the memory branch; proposals cannot redirect canonical identity.
        canonical_id: undefined,
        type: priorCause?.type ?? cause.type,
        title: priorCause?.title ?? cause.title,
        test_name: priorCause?.test_name ?? cause.test_name,
        test_names: testNames.length > 0 ? testNames : undefined,
        error_pattern: priorCause?.error_pattern ?? cause.error_pattern,
        matchers: priorCause?.matchers,
        issue_url: priorCause?.issue_url,
        job_ids: jobIds,
        job_names: jobNames,
    });
}

function mergeCurrentCauses(existing, current) {
    return {
        ...existing,
        test_names: unique([...(existing.test_names ?? []), ...(current.test_names ?? [])]),
        job_ids: unique([...existing.job_ids, ...current.job_ids]),
        job_names: unique([...existing.job_names, ...current.job_names]),
    };
}

function validateTrackedJobsHaveCauses(analysis) {
    const missingJobs = analysis.failed_jobs
        .filter(job => trackedClassifications.has(job.classification))
        .filter(job => job.cause_ids.length === 0)
        .map(job => `${job.name} (${job.id})`);

    if (missingJobs.length > 0) {
        throw new Error(`Tracked failed jobs are missing cause references: ${missingJobs.join(', ')}.`);
    }
}

function normalizeTestName(testName) {
    const displayName = String(testName ?? '').trim();
    const argumentStart = displayName.indexOf('(');
    const canonicalName = argumentStart > 0 ? displayName.slice(0, argumentStart) : displayName;

    return canonicalName
        .replace(/\s+/g, ' ')
        .toLowerCase();
}

function allTestNames(cause) {
    return unique([
        cause.test_name,
        ...(Array.isArray(cause.test_names) ? cause.test_names : []),
    ].filter(Boolean));
}

function matchesConfiguredPattern(pattern, value) {
    if (typeof pattern === 'string') {
        return value.toLowerCase().includes(pattern.toLowerCase());
    }

    if (pattern?.regex) {
        try {
            return new RegExp(pattern.regex, 'i').test(value);
        } catch {
            return false;
        }
    }

    return false;
}

function matchesExplicitMatcher(matcher, evidence) {
    if (matcher.kind === 'error-literal' && typeof matcher.value === 'string') {
        return evidence.toLowerCase().includes(matcher.value.toLowerCase());
    }

    if (matcher.kind === 'error-regex' && typeof matcher.pattern === 'string') {
        return new RegExp(matcher.pattern, matcher.flags ?? 'i').test(evidence);
    }

    throw new Error(`Unsupported cause matcher kind '${matcher.kind ?? ''}'.`);
}

function firstObservedAt(cause) {
    const dates = (cause.occurrences ?? [])
        .map(occurrence => occurrence.observed_at)
        .filter(Boolean)
        .sort();
    return dates[0] ?? '9999-12-31T23:59:59Z';
}

function unique(values) {
    return [...new Set(values)];
}

function uniqueById(causes) {
    return [...new Map(causes.map(cause => [cause.id, cause])).values()];
}

function removeUndefined(value) {
    return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined));
}

function readJsonFiles(directory) {
    if (!fs.existsSync(directory)) {
        return [];
    }

    return fs.readdirSync(directory)
        .filter(fileName => fileName.endsWith('.json'))
        .sort()
        .map(fileName => {
            const cause = JSON.parse(fs.readFileSync(path.join(directory, fileName), 'utf8'));
            if (fileName !== `${cause.id}.json`) {
                throw new Error(`Cause file '${fileName}' does not match its ID '${cause.id}'.`);
            }

            return cause;
        });
}

function migratePriorCauseFiles(directory, migrations) {
    if (!fs.existsSync(directory) || migrations.length === 0) {
        return;
    }

    const canonicalByLegacyId = new Map(
        migrations.map(migration => [migration.legacy_id, migration.canonical_id]));

    for (const migration of migrations) {
        const legacyPath = path.join(directory, `${migration.legacy_id}.json`);
        const canonicalPath = path.join(directory, `${migration.canonical_id}.json`);
        if (!fs.existsSync(legacyPath)) {
            throw new Error(`Legacy cause file '${migration.legacy_id}.json' does not exist.`);
        }
        const canonicalPathExists = fs.existsSync(canonicalPath);
        const legacyFile = fs.statSync(legacyPath);
        const canonicalFile = canonicalPathExists ? fs.statSync(canonicalPath) : undefined;
        const pathsReferToSameFile = canonicalFile &&
            legacyFile.dev === canonicalFile.dev &&
            legacyFile.ino === canonicalFile.ino;
        if (canonicalPathExists && !pathsReferToSameFile) {
            throw new Error(
                `Cannot migrate legacy cause '${migration.legacy_id}' because '${migration.canonical_id}' already exists.`);
        }

        const cause = JSON.parse(fs.readFileSync(legacyPath, 'utf8'));
        cause.id = migration.canonical_id;
        // A temporary path is required for case-only renames on case-insensitive file systems.
        const temporaryPath = `${canonicalPath}.migrating`;
        fs.writeFileSync(temporaryPath, `${JSON.stringify(cause, null, 2)}\n`);
        fs.rmSync(legacyPath);
        fs.renameSync(temporaryPath, canonicalPath);
    }

    // Aliases are separate records, so their targets must move with the canonical cause.
    for (const fileName of fs.readdirSync(directory).filter(fileName => fileName.endsWith('.json'))) {
        const causePath = path.join(directory, fileName);
        const cause = JSON.parse(fs.readFileSync(causePath, 'utf8'));
        const canonicalId = canonicalByLegacyId.get(cause.canonical_id);
        if (canonicalId) {
            cause.canonical_id = canonicalId;
            fs.writeFileSync(causePath, `${JSON.stringify(cause, null, 2)}\n`);
        }
    }
}

function runCli(args) {
    if (args.length !== 4) {
        throw new Error(
            'Usage: node analyze-ci-failure-cause-resolver.js <analysis-file> <causes-directory> <prior-causes-directory> <retry-patterns-file>');
    }

    const [analysisFile, causesDirectory, priorCausesDirectory, retryPatternsFile] = args;
    const result = resolveCauses({
        analysis: JSON.parse(fs.readFileSync(analysisFile, 'utf8')),
        causes: readJsonFiles(causesDirectory),
        priorCauses: readJsonFiles(priorCausesDirectory),
        retryPatterns: JSON.parse(fs.readFileSync(retryPatternsFile, 'utf8')),
    });

    migratePriorCauseFiles(priorCausesDirectory, result.priorCauseMigrations);
    fs.writeFileSync(analysisFile, `${JSON.stringify(result.analysis, null, 2)}\n`);
    fs.mkdirSync(causesDirectory, { recursive: true });
    for (const fileName of fs.readdirSync(causesDirectory)) {
        if (fileName.endsWith('.json')) {
            fs.rmSync(path.join(causesDirectory, fileName));
        }
    }
    for (const cause of result.causes) {
        fs.writeFileSync(
            path.join(causesDirectory, `${cause.id}.json`),
            `${JSON.stringify(cause, null, 2)}\n`);
    }

    for (const canonicalization of result.canonicalizations) {
        console.log(`Canonicalized ${canonicalization.proposed_id} -> ${canonicalization.canonical_id}`);
    }
    for (const migration of result.priorCauseMigrations) {
        console.log(`Migrated legacy cause ${migration.legacy_id} -> ${migration.canonical_id}`);
    }
}

if (require.main === module) {
    try {
        runCli(process.argv.slice(2));
    } catch (error) {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    }
}

module.exports = {
    normalizeTestName,
    resolveCauses,
};
