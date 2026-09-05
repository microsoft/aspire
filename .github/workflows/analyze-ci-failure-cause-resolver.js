const fs = require('node:fs');
const path = require('node:path');

const trackedClassifications = new Set(['flaky-test', 'transient-infra', 'main-repository-breakage']);
const supportedCauseTypes = new Set(['flaky-test', 'infra-failure', 'main-repository-breakage']);
const safeCauseIdPattern = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const causeTypeJobClassifications = new Map([
    ['infra-failure', 'transient-infra'],
    ['flaky-test', 'flaky-test'],
    ['main-repository-breakage', 'main-repository-breakage'],
]);

function resolveCauses({
    analysis,
    causes,
    priorCauses = [],
    retryPatterns = {},
    trustedFailedJobs = analysis?.failed_jobs,
}) {
    validateInputs(analysis, causes, priorCauses);
    validateRetryPatternCauseIds(retryPatterns);
    validateProposedCauseIds(analysis, causes);

    priorCauses = priorCauses.filter(
        cause => cause && typeof cause.id === 'string' && cause.id.length > 0);

    const priorById = new Map(priorCauses.map(cause => [cause.id, cause]));
    // Historical memory predates the slug contract. Match sanitized proposals back to those
    // records so fixing an ID does not split one cause into old and new identities.
    const priorByNormalizedId = buildPriorByNormalizedId(priorCauses);
    const failedJobsById = new Map(analysis.failed_jobs.map(job => [job.id, job]));
    const trustedFailedJobsById = buildTrustedFailedJobsById(analysis, trustedFailedJobs);
    const canonicalizations = [];
    const normalizedById = new Map();
    const proposedToCanonical = new Map();
    const priorCauseMigrations = new Map();
    const priorCauseAliases = new Map();
    const compatibleNormalizedPriorAliases = new Set();
    const hasPriorMatchByCanonicalId = new Map();

    for (const cause of causes) {
        const jobIds = resolveCauseJobIds(cause, analysis, failedJobsById, trustedFailedJobsById);
        const jobNames = jobIds.map(jobId => trustedFailedJobsById.get(jobId).name);
        const evidence = buildEvidence(cause, analysis, jobNames);

        const proposedPriorCause = findPriorCauseById(cause.id, priorById, priorByNormalizedId);
        const proposedCanonicalCause = proposedPriorCause
            ? resolveAlias(proposedPriorCause, priorById)
            : undefined;
        const proposedAlias = proposedPriorCause?.canonical_id
            ? proposedCanonicalCause
            : undefined;
        let testNameMatch;
        let sameTestCanonicalExtras = [];
        let retryPatternMatch;
        let explicitMatcherMatch;

        if (cause.type === 'flaky-test') {
            const sameTestPriorCauses = findPriorCausesByTestName(cause, priorCauses, priorById);
            const proposedAliasMatchesTest = proposedAlias &&
                [sameTestPriorCauses.canonical, ...sameTestPriorCauses.extras]
                    .some(candidate => candidate?.id === proposedAlias.id);
            if (!proposedAlias || proposedAliasMatchesTest) {
                testNameMatch = sameTestPriorCauses.canonical;
                sameTestCanonicalExtras = sameTestPriorCauses.extras;
            }
        }
        if (!proposedAlias) {
            retryPatternMatch = cause.type === 'infra-failure'
                ? findPriorCauseByRetryPattern(
                    evidence,
                    jobNames,
                    retryPatterns,
                    priorById,
                    priorByNormalizedId)
                : undefined;
            explicitMatcherMatch = findPriorCauseByExplicitMatcher(evidence, priorCauses, priorById);
            const crossMechanismMatches = uniqueById(
                [testNameMatch, retryPatternMatch, explicitMatcherMatch].filter(Boolean));

            if (crossMechanismMatches.length > 1) {
                throw new Error(
                    `Failure matched conflicting canonical prior causes: ${crossMechanismMatches.map(match => match.id).join(', ')}.`);
            }
        }

        // Normalized test identity converges compatible historical roots. An explicit alias is
        // otherwise authoritative, while retry patterns and matchers cover cross-test root causes.
        const canonicalPriorCause =
            testNameMatch ??
            proposedAlias ??
            retryPatternMatch ??
            explicitMatcherMatch ??
            findPriorCauseByExistingId(cause, priorById, priorByNormalizedId);
        if (canonicalPriorCause?.type) {
            validateCauseType(canonicalPriorCause);
            if (canonicalPriorCause.type !== cause.type) {
                throw new Error(
                    `Cause '${cause.id}' has type '${cause.type}', but canonical cause ` +
                    `'${canonicalPriorCause.id}' has type '${canonicalPriorCause.type}'.`);
            }
        }

        const priorCauseId = canonicalPriorCause?.id;
        const canonicalId = getCanonicalCauseId(cause.id, priorCauseId);
        // A legacy root can normalize to an ID already occupied by a compatible newer root.
        // Keep that safe file as the physical canonical and rewrite the legacy family as aliases
        // instead of asking the migration step to overwrite the existing destination.
        const normalizedDestinationPriorCause =
            priorCauseId && priorCauseId !== canonicalId
                ? sameTestCanonicalExtras.find(extra => extra.id === canonicalId)
                : undefined;
        const canonicalPriorRecords = canonicalPriorCause
            ? findPriorRecordsForCanonicalCause(canonicalPriorCause.id, priorCauses)
            : [];
        const normalizedCanonicalAliasRecord = canonicalPriorRecords.find(record =>
            record.id !== canonicalId &&
            record.canonical_id === canonicalId &&
            normalizeCauseId(record.id) === canonicalId &&
            record.type === cause.type &&
            allTestNames(record).some(testName =>
                normalizeTestName(testName) === normalizeTestName(cause.test_name)));
        const usesCompatibleNormalizedDestination =
            normalizedDestinationPriorCause !== undefined ||
            normalizedCanonicalAliasRecord !== undefined;
        const normalizedLegacyRecords = normalizedDestinationPriorCause
            ? findPriorRecordsForCanonicalCause(priorCauseId, priorCauses)
            : [];
        for (const record of normalizedLegacyRecords) {
            addPriorCauseAlias(priorCauseAliases, record.id, canonicalId);
            compatibleNormalizedPriorAliases.add(record.id);
        }
        const supersededPriorCause =
            proposedCanonicalCause?.id !== priorCauseId &&
            proposedCanonicalCause?.id !== canonicalId
            ? proposedCanonicalCause
            : undefined;
        if (supersededPriorCause) {
            validateCauseType(supersededPriorCause);
            if (supersededPriorCause.type !== cause.type) {
                throw new Error(
                    `Cause '${cause.id}' of type '${cause.type}' cannot alias prior cause type ` +
                    `'${supersededPriorCause.type}'.`);
            }
            addPriorCauseAlias(priorCauseAliases, supersededPriorCause.id, canonicalId);
        }
        const sameTestExtraRecords = sameTestCanonicalExtras.flatMap(extra => {
            validateCauseType(extra);
            if (extra.type !== cause.type) {
                throw new Error(
                    `Cause '${cause.id}' of type '${cause.type}' cannot alias prior cause type ` +
                    `'${extra.type}'.`);
            }
            const records = findPriorRecordsForCanonicalCause(extra.id, priorCauses);
            for (const record of records) {
                addPriorCauseAlias(priorCauseAliases, record.id, canonicalId);
            }
            return records;
        });
        const sameTestExtraAliases = unique(sameTestCanonicalExtras.flatMap(extra => [
            extra.id,
            ...(extra.aliases ?? []),
            ...sameTestExtraRecords
                .filter(record => record.id !== extra.id)
                .map(record => record.id),
        ])).sort();
        const compatibleNormalizedRecords = usesCompatibleNormalizedDestination
            ? uniqueById([...canonicalPriorRecords, ...sameTestExtraRecords])
            : [];
        for (const record of compatibleNormalizedRecords) {
            validateCauseType(record);
            if (record.type !== cause.type) {
                throw new Error(
                    `Cause '${cause.id}' of type '${cause.type}' cannot alias prior cause type ` +
                    `'${record.type}'.`);
            }
        }
        const normalizationPriorCause = usesCompatibleNormalizedDestination
            ? {
                ...(normalizedDestinationPriorCause ?? canonicalPriorCause),
                test_names: unique(
                    compatibleNormalizedRecords.flatMap(record => allTestNames(record))).sort(),
            }
            : canonicalPriorCause;
        const compatibleNormalizedIssueUrl = [...compatibleNormalizedRecords]
            .sort((left, right) => {
                const dateComparison = firstObservedAt(left).localeCompare(firstObservedAt(right));
                return dateComparison !== 0 ? dateComparison : left.id.localeCompare(right.id);
            })
            .find(record => record.issue_url)
            ?.issue_url;
        const canonicalFamilyAliases = unique(canonicalPriorRecords
            .filter(record => record.type === undefined || record.type === cause.type)
            .flatMap(record => [record.id, ...(record.aliases ?? [])]))
            .sort();
        const aliases = unique([
            ...(canonicalPriorCause?.aliases ?? []),
            ...(proposedAlias && proposedPriorCause.id !== canonicalId ? [proposedPriorCause.id] : []),
            ...(priorCauseId && priorCauseId !== canonicalId ? [priorCauseId] : []),
            ...(supersededPriorCause
                ? [supersededPriorCause.id, ...(supersededPriorCause.aliases ?? [])]
                : []),
            ...canonicalFamilyAliases,
            ...normalizedLegacyRecords.map(record => record.id),
            ...compatibleNormalizedRecords.flatMap(record => [
                record.id,
                ...(record.aliases ?? []),
            ]),
            ...sameTestExtraAliases,
        ]).filter(alias => alias !== canonicalId);
        if (sameTestCanonicalExtras.length > 0) {
            aliases.sort();
        }
        const normalizedCause = normalizeCause(
            cause,
            normalizationPriorCause,
            canonicalId,
            jobIds,
            jobNames,
            aliases,
            compatibleNormalizedIssueUrl ??
                canonicalPriorCause?.issue_url ??
                sameTestCanonicalExtras.find(extra => extra.issue_url)?.issue_url ??
                supersededPriorCause?.issue_url);
        if (usesCompatibleNormalizedDestination) {
            normalizedCause.test_names?.sort();
            normalizedCause.aliases?.sort();
        }
        validateCauseType(normalizedCause);
        proposedToCanonical.set(cause.id, canonicalId);
        hasPriorMatchByCanonicalId.set(
            canonicalId,
            hasPriorMatchByCanonicalId.get(canonicalId) === true || canonicalPriorCause !== undefined);

        if (priorCauseId && priorCauseId !== canonicalId && !normalizedDestinationPriorCause) {
            priorCauseMigrations.set(priorCauseId, canonicalId);
        }

        if (cause.id !== canonicalId) {
            canonicalizations.push({ proposed_id: cause.id, canonical_id: canonicalId });
        }

        const existing = normalizedById.get(canonicalId);
        if (existing && existing.type !== normalizedCause.type) {
            throw new Error(
                `Canonical cause '${canonicalId}' cannot merge current causes with types ` +
                `'${existing.type}' and '${normalizedCause.type}'.`);
        }
        normalizedById.set(
            canonicalId,
            existing ? mergeCurrentCauses(existing, normalizedCause) : normalizedCause);
    }

    coalesceFreshFlakyCauses({
        normalizedById,
        hasPriorMatchByCanonicalId,
        proposedToCanonical,
        canonicalizations,
        priorCauseMigrations,
        priorCauseAliases,
    });

    for (const [legacyId, canonicalId] of priorCauseAliases) {
        const normalizedLegacyId = normalizeCauseId(legacyId);
        const isCompatibleNormalizedAlias =
            compatibleNormalizedPriorAliases.has(legacyId) &&
            canonicalId === normalizedLegacyId;
        if (normalizedById.has(normalizedLegacyId) && !isCompatibleNormalizedAlias) {
            const collision = legacyId === normalizedLegacyId
                ? 'also resolves to it'
                : `also resolves to '${normalizedLegacyId}'`;
            throw new Error(
                `Cause resolution cannot alias prior cause '${legacyId}' because the current batch ` +
                `${collision} (proposed alias target '${canonicalId}').`);
        }
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
            name: trustedFailedJobsById.get(job.id).name,
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

    validateCauseJobAttribution(normalizedAnalysis, normalizedCauses, trustedFailedJobs);

    return {
        analysis: normalizedAnalysis,
        causes: normalizedCauses,
        canonicalizations,
        priorCauseMigrations: toSortedCauseRemappings(priorCauseMigrations),
        priorCauseAliases: toSortedCauseRemappings(priorCauseAliases),
    };
}

function toSortedCauseRemappings(remappings) {
    return [...remappings]
        .map(([legacyId, canonicalId]) => ({
            legacy_id: legacyId,
            canonical_id: canonicalId,
        }))
        .sort((left, right) =>
            left.legacy_id.localeCompare(right.legacy_id) ||
            left.canonical_id.localeCompare(right.canonical_id));
}

function coalesceFreshFlakyCauses({
    normalizedById,
    hasPriorMatchByCanonicalId,
    proposedToCanonical,
    canonicalizations,
    priorCauseMigrations,
    priorCauseAliases,
}) {
    const groupsByTestIdentity = new Map();
    for (const cause of normalizedById.values()) {
        if (cause.type !== 'flaky-test' || !cause.test_name) {
            continue;
        }

        const testIdentity = normalizeTestName(cause.test_name);
        const group = groupsByTestIdentity.get(testIdentity) ?? [];
        group.push(cause);
        groupsByTestIdentity.set(testIdentity, group);
    }

    const remappedCauseIds = new Map();
    for (const [testIdentity, group] of groupsByTestIdentity) {
        const authoritativeCauseIds = group
            .filter(cause => hasPriorMatchByCanonicalId.get(cause.id) === true)
            .map(cause => cause.id)
            .sort();
        if (authoritativeCauseIds.length > 1) {
            throw new Error(
                `Flaky test identity '${testIdentity}' resolves to conflicting authoritative canonical causes ` +
                `'${authoritativeCauseIds.join("' and '")}'.`);
        }

        const freshCauses = group.filter(
            cause => hasPriorMatchByCanonicalId.get(cause.id) !== true);
        freshCauses.sort((left, right) => left.id.localeCompare(right.id));
        let owner;
        let absorbedCauses;
        if (authoritativeCauseIds.length === 1) {
            owner = normalizedById.get(authoritativeCauseIds[0]);
            absorbedCauses = freshCauses;
        } else {
            if (freshCauses.length < 2) {
                continue;
            }
            [owner, ...absorbedCauses] = freshCauses;
        }

        if (absorbedCauses.length === 0) {
            continue;
        }

        let merged = owner;
        for (const absorbed of absorbedCauses) {
            merged = mergeCurrentCauses(merged, {
                ...absorbed,
                aliases: unique([absorbed.id, ...(absorbed.aliases ?? [])]),
            });
            normalizedById.delete(absorbed.id);
            remappedCauseIds.set(absorbed.id, owner.id);
        }
        normalizedById.set(owner.id, merged);
    }

    if (remappedCauseIds.size === 0) {
        return;
    }

    const remap = causeId => remappedCauseIds.get(causeId) ?? causeId;
    for (const [proposedId, canonicalId] of proposedToCanonical) {
        proposedToCanonical.set(proposedId, remap(canonicalId));
    }
    for (const [legacyId, canonicalId] of priorCauseMigrations) {
        priorCauseMigrations.set(legacyId, remap(canonicalId));
    }
    for (const [legacyId, canonicalId] of priorCauseAliases) {
        priorCauseAliases.set(legacyId, remap(canonicalId));
    }

    const remappedCanonicalizations = canonicalizations.map(canonicalization => ({
        ...canonicalization,
        canonical_id: remap(canonicalization.canonical_id),
    }));
    for (const [absorbedId, canonicalId] of remappedCauseIds) {
        remappedCanonicalizations.push({
            proposed_id: absorbedId,
            canonical_id: canonicalId,
        });
    }
    const uniqueCanonicalizations = new Map(remappedCanonicalizations.map(
        canonicalization => [
            `${canonicalization.proposed_id}\0${canonicalization.canonical_id}`,
            canonicalization,
        ]));
    canonicalizations.splice(
        0,
        canonicalizations.length,
        ...[...uniqueCanonicalizations.values()].sort((left, right) =>
            left.proposed_id.localeCompare(right.proposed_id) ||
            left.canonical_id.localeCompare(right.canonical_id)));
}

function buildTrustedFailedJobsById(analysis, trustedFailedJobs) {
    if (!Array.isArray(trustedFailedJobs)) {
        throw new Error('Trusted failed jobs must be an array.');
    }

    const trustedFailedJobsById = new Map();
    for (const job of trustedFailedJobs) {
        if (!job || typeof job.id !== 'number' || typeof job.name !== 'string' || job.name.length === 0) {
            throw new Error('Trusted failed jobs must have numeric IDs and non-empty names.');
        }
        if (trustedFailedJobsById.has(job.id)) {
            throw new Error(`Trusted failed job ID '${job.id}' is duplicated.`);
        }
        trustedFailedJobsById.set(job.id, job);
    }

    for (const job of analysis.failed_jobs) {
        if (!trustedFailedJobsById.has(job.id)) {
            throw new Error(`Analysis references failed job ID '${job.id}' outside the trusted scope.`);
        }
    }

    return trustedFailedJobsById;
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

function validateRetryPatternCauseIds(retryPatterns) {
    for (const [index, pattern] of (retryPatterns.jobFailurePatterns ?? []).entries()) {
        if (pattern?.causeId !== undefined &&
            (typeof pattern.causeId !== 'string' || !safeCauseIdPattern.test(pattern.causeId))) {
            throw new Error(
                `jobFailurePatterns[${index}].causeId '${String(pattern.causeId)}' must be a safe cause ID.`);
        }
    }
}

function validateCauseType(cause) {
    if (!supportedCauseTypes.has(cause.type)) {
        throw new Error(`Cause '${cause.id}' has unsupported type '${cause.type ?? ''}'.`);
    }
}

function validateProposedCauseIds(analysis, causes) {
    const proposedCauseIds = [...analysis.causes, ...causes.map(cause => cause.id)];
    for (const causeId of proposedCauseIds) {
        if (!safeCauseIdPattern.test(causeId)) {
            throw new Error(
                `Cause ID '${causeId}' must already be a canonical lowercase slug.`);
        }
    }
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

function resolveCauseJobIds(cause, analysis, failedJobsById, trustedFailedJobsById) {
    let jobIds = cause.job_ids;

    if (!Array.isArray(jobIds) || jobIds.length === 0) {
        throw new Error(`Cause '${cause.id}' must reference at least one failed job.`);
    }

    jobIds = unique(jobIds);
    for (const jobId of jobIds) {
        const failedJob = failedJobsById.get(jobId);
        const trustedFailedJob = trustedFailedJobsById.get(jobId);
        if (!failedJob) {
            throw new Error(`Cause '${cause.id}' references unknown failed job ID '${jobId}'.`);
        }
        if (!isCauseCompatibleWithFailedJob(cause, failedJob, trustedFailedJob, analysis.failed_tests)) {
            throw new Error(
                `Cause '${cause.id}' references job '${failedJob.name}', which is classified as '${failedJob.classification}'.`);
        }
    }

    if (cause.test_name) {
        const normalizedTestName = normalizeTestName(cause.test_name);
        const missingJobIds = jobIds.filter(jobId => {
            const jobName = trustedFailedJobsById.get(jobId).name;
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

function buildEvidence(cause, analysis, jobNames) {
    const trustedJobNames = new Set(jobNames);
    const causeTestNames = new Set(cause.test_name ? [normalizeTestName(cause.test_name)] : []);
    const failedTests = analysis.failed_tests.filter(test =>
        trustedJobNames.has(test.job) && causeTestNames.has(normalizeTestName(test.name)));

    return [
        cause.title,
        cause.error_pattern,
        ...failedTests.flatMap(test => [test.name, test.error, test.stack_trace]),
    ].filter(value => typeof value === 'string' && value.length > 0).join('\n');
}

function findPriorCauseByExistingId(cause, priorById, priorByNormalizedId) {
    const priorCause = findPriorCauseById(cause.id, priorById, priorByNormalizedId);
    return priorCause ? resolveAlias(priorCause, priorById) : undefined;
}

function findPriorCauseById(causeId, priorById, priorByNormalizedId) {
    return priorById.get(causeId) ?? priorByNormalizedId.get(causeId);
}

function findPriorCausesByTestName(cause, priorCauses, priorById) {
    if (!cause.test_name) {
        return { canonical: undefined, extras: [] };
    }

    const normalizedTestName = normalizeTestName(cause.test_name);
    const candidates = priorCauses.filter(prior =>
        allTestNames(prior).some(testName => normalizeTestName(testName) === normalizedTestName));
    const typeCompatibleCandidates = candidates.filter(prior => prior.type === cause.type);
    const unsupportedCandidates = candidates.filter(prior => !supportedCauseTypes.has(prior.type));

    const canonicalCandidates = sortCanonicalCauses(
        typeCompatibleCandidates.length > 0 ? typeCompatibleCandidates : unsupportedCandidates,
        priorById);
    return {
        canonical: canonicalCandidates[0],
        extras: typeCompatibleCandidates.length > 0 ? canonicalCandidates.slice(1) : [],
    };
}

function findPriorCauseByRetryPattern(
    evidence,
    jobNames,
    retryPatterns,
    priorById,
    priorByNormalizedId) {
    const matchingCauseIds = unique((retryPatterns.jobFailurePatterns ?? [])
        .filter(pattern => pattern.enabled !== false)
        .filter(pattern => pattern.causeId)
        .filter(pattern => pattern.output || pattern.jobName)
        .filter(pattern => !pattern.output || matchesConfiguredPattern(pattern.output, evidence))
        .filter(pattern => !pattern.jobName || jobNames.some(jobName => matchesConfiguredPattern(pattern.jobName, jobName)))
        .map(pattern => pattern.causeId));

    if (matchingCauseIds.length > 1) {
        throw new Error(`Failure matched multiple retry-pattern cause IDs: ${matchingCauseIds.join(', ')}.`);
    }

    if (matchingCauseIds.length === 0) {
        return undefined;
    }

    const causeId = matchingCauseIds[0];
    const priorCause = findPriorCauseById(causeId, priorById, priorByNormalizedId);
    return priorCause ? resolveAlias(priorCause, priorById) : { id: causeId };
}

function findPriorCauseByExplicitMatcher(evidence, priorCauses, priorById) {
    const candidates = [];

    for (const priorCause of priorCauses) {
        let matched = false;
        for (const [index, matcher] of (priorCause.matchers ?? []).entries()) {
            matched = matchesExplicitMatcher(matcher, evidence, priorCause.id, index) || matched;
        }

        if (matched) {
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
    return sortCanonicalCauses(candidates, priorById)[0];
}

function sortCanonicalCauses(candidates, priorById) {
    const canonicalCandidates = uniqueById(candidates.map(cause => resolveAlias(cause, priorById)));
    return canonicalCandidates.sort((left, right) => {
        const dateComparison = firstObservedAt(left).localeCompare(firstObservedAt(right));
        return dateComparison !== 0 ? dateComparison : left.id.localeCompare(right.id);
    });
}

function addPriorCauseAlias(priorCauseAliases, legacyId, canonicalId) {
    if (legacyId === canonicalId) {
        return;
    }

    const existingAliasTarget = priorCauseAliases.get(legacyId);
    if (existingAliasTarget && existingAliasTarget !== canonicalId) {
        throw new Error(
            `Prior cause '${legacyId}' matched conflicting canonical causes ` +
            `'${existingAliasTarget}' and '${canonicalId}'.`);
    }
    priorCauseAliases.set(legacyId, canonicalId);
}

function findPriorRecordsForCanonicalCause(canonicalId, priorCauses) {
    // Alias records can form chains. Walk the reverse links so every record that
    // ultimately named the absorbed root is rewritten directly to the new owner.
    const matchingIds = new Set([canonicalId]);
    let added;
    do {
        added = false;
        for (const prior of priorCauses) {
            if (!matchingIds.has(prior.id) && matchingIds.has(prior.canonical_id)) {
                matchingIds.add(prior.id);
                added = true;
            }
        }
    } while (added);

    return priorCauses
        .filter(prior => matchingIds.has(prior.id))
        .sort((left, right) => left.id.localeCompare(right.id));
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

function normalizeCause(cause, priorCause, canonicalId, jobIds, jobNames, aliases, issueUrl) {
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
        issue_url: issueUrl,
        aliases: aliases.length > 0 ? aliases : undefined,
        job_ids: jobIds,
        job_names: jobNames,
    });
}

function mergeCurrentCauses(existing, current) {
    const aliases = unique([...(existing.aliases ?? []), ...(current.aliases ?? [])]);
    return removeUndefined({
        ...existing,
        test_names: unique([...(existing.test_names ?? []), ...(current.test_names ?? [])]),
        aliases: aliases.length > 0 ? aliases : undefined,
        job_ids: unique([...existing.job_ids, ...current.job_ids]),
        job_names: unique([...existing.job_names, ...current.job_names]),
    });
}

function validateCauseJobAttribution(analysis, causes, trustedFailedJobs) {
    if (!analysis || !Array.isArray(analysis.failed_jobs) || !Array.isArray(causes)) {
        throw new Error('Cause job attribution requires failed_jobs and causes arrays.');
    }
    if (!Array.isArray(trustedFailedJobs) ||
        !trustedFailedJobs.every(job => job && Number.isInteger(job.id))) {
        throw new Error('Trusted failed jobs are invalid.');
    }

    const trustedJobsById = new Map(trustedFailedJobs.map(job => [job.id, job]));
    if (trustedJobsById.size !== trustedFailedJobs.length) {
        throw new Error('Trusted failed job IDs must be unique.');
    }

    const trackedJobs = analysis.failed_jobs.filter(
        job => trackedClassifications.has(job.classification));
    const failedJobsById = new Map(analysis.failed_jobs.map(job => [job.id, job]));
    const failedTests = Array.isArray(analysis.failed_tests) ? analysis.failed_tests : [];
    const coveredJobIds = new Set();
    for (const cause of causes) {
        if (!Array.isArray(cause?.job_ids) ||
            cause.job_ids.length === 0 ||
            !cause.job_ids.every(Number.isInteger)) {
            throw new Error(`Cause '${cause?.id ?? ''}' must contain non-empty numeric job_ids.`);
        }

        for (const jobId of cause.job_ids) {
            const trustedJob = trustedJobsById.get(jobId);
            const failedJob = failedJobsById.get(jobId);
            if (!trustedJob || !failedJob) {
                throw new Error(`Cause '${cause.id}' references untrusted failed job ID '${jobId}'.`);
            }
            if (!isCauseCompatibleWithFailedJob(cause, failedJob, trustedJob, failedTests)) {
                throw new Error(`Cause '${cause.id}' of type '${cause.type}' cannot reference job ID '${jobId}' classified as '${failedJob.classification}'.`);
            }
            coveredJobIds.add(jobId);
        }
    }

    const missingJobs = trackedJobs
        .filter(job => !coveredJobIds.has(job.id))
        .map(job => typeof job.name === 'string' && job.name.length > 0
            ? `${job.name} (${job.id})`
            : String(job.id));

    if (missingJobs.length > 0) {
        throw new Error(`Tracked failed jobs are missing cause references: ${missingJobs.join(', ')}.`);
    }

    return true;
}

function isCauseCompatibleWithFailedJob(cause, failedJob, trustedJob, failedTests) {
    if (causeTypeJobClassifications.get(cause.type) === failedJob.classification) {
        return true;
    }

    const causeTestName = normalizeTestName(cause.test_name);
    return cause.type === 'flaky-test' &&
        causeTestName.length > 0 &&
        typeof trustedJob?.name === 'string' &&
        trustedJob.name.length > 0 &&
        failedTests.some(test =>
            test?.classification === 'flaky' &&
            test.job === trustedJob.name &&
            normalizeTestName(test.name) === causeTestName);
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

function matchesExplicitMatcher(matcher, evidence, priorCauseId, matcherIndex) {
    if (matcher.kind === 'error-literal' && typeof matcher.value === 'string') {
        return evidence.toLowerCase().includes(matcher.value.toLowerCase());
    }

    if (matcher.kind === 'error-regex' && typeof matcher.pattern === 'string') {
        const flags = matcher.flags ?? 'i';
        try {
            return new RegExp(matcher.pattern, flags).test(evidence);
        } catch (error) {
            throw new Error(
                `Prior cause '${priorCauseId}' matcher ${matcherIndex} has invalid regular expression ` +
                `'${matcher.pattern}' with flags '${flags}': ${error.message}`);
        }
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
        if (typeof migration.legacy_id !== 'string' || /[\\/]/.test(migration.legacy_id) ||
            !safeCauseIdPattern.test(migration.canonical_id)) {
            throw new Error(
                `Cannot migrate unsafe cause IDs '${migration.legacy_id ?? ''}' -> '${migration.canonical_id ?? ''}'.`);
        }

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

function writePriorCauseAliases(directory, aliases) {
    for (const alias of aliases) {
        if (typeof alias.legacy_id !== 'string' || /[\\/]/.test(alias.legacy_id) ||
            !safeCauseIdPattern.test(alias.canonical_id)) {
            throw new Error(
                `Cannot alias unsafe cause IDs '${alias.legacy_id ?? ''}' -> '${alias.canonical_id ?? ''}'.`);
        }

        const aliasPath = path.join(directory, `${alias.legacy_id}.json`);
        if (!fs.existsSync(aliasPath)) {
            throw new Error(`Prior cause file '${alias.legacy_id}.json' does not exist.`);
        }

        const cause = JSON.parse(fs.readFileSync(aliasPath, 'utf8'));
        cause.canonical_id = alias.canonical_id;
        fs.writeFileSync(aliasPath, `${JSON.stringify(cause, null, 2)}\n`);
    }
}

function runCli(args) {
    if (args.length < 4 || args.length > 5) {
        throw new Error(
            'Usage: node analyze-ci-failure-cause-resolver.js <analysis-file> <causes-directory> <prior-causes-directory> <retry-patterns-file> [trusted-failed-jobs-file]');
    }

    const [
        analysisFile,
        causesDirectory,
        priorCausesDirectory,
        retryPatternsFile,
        trustedFailedJobsFile,
    ] = args;
    const analysis = JSON.parse(fs.readFileSync(analysisFile, 'utf8'));
    const result = resolveCauses({
        analysis,
        causes: readJsonFiles(causesDirectory),
        priorCauses: readJsonFiles(priorCausesDirectory),
        retryPatterns: JSON.parse(fs.readFileSync(retryPatternsFile, 'utf8')),
        trustedFailedJobs: trustedFailedJobsFile
            ? JSON.parse(fs.readFileSync(trustedFailedJobsFile, 'utf8'))
            : analysis.failed_jobs,
    });

    migratePriorCauseFiles(priorCausesDirectory, result.priorCauseMigrations);
    writePriorCauseAliases(priorCausesDirectory, result.priorCauseAliases);
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
    for (const alias of result.priorCauseAliases) {
        console.log(`Aliased prior cause ${alias.legacy_id} -> ${alias.canonical_id}`);
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
    validateCauseJobAttribution,
};
