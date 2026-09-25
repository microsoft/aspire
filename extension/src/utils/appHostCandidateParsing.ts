import { extensionLogOutputChannel } from './logging';
import { isSamePath } from './paths/comparison';
import type { AppHostCandidate, AppHostProjectSearchResult, CandidateAppHostDisplayInfo, IncrementalCandidateCallback, LegacyAppHostProjectSearchResult } from './appHostCandidateTypes';

export function createLsStreamCandidateHandler(onCandidate: IncrementalCandidateCallback): (line: string) => void {
    return line => {
        const trimmed = line.trim();
        if (!trimmed) {
            return;
        }

        let parsed: unknown;
        try {
            // `aspire ls --format json --stream` emits newline-delimited JSON, one candidate per line:
            //   {"path":"/repo/AppHost/AppHost.csproj","language":"csharp","status":"buildable"}
            // Treat malformed lines as a failed stream instead of accepting a truncated partial result.
            parsed = JSON.parse(trimmed);
        }
        catch {
            throw new Error('aspire ls --stream returned malformed JSON.');
        }

        if (!isLsCandidate(parsed)) {
            throw new Error('aspire ls --stream returned a candidate with an unexpected shape.');
        }

        onCandidate(toDisplayCandidate(parsed));
    };
}

export function parseCandidateOutput(output: string): CandidateAppHostDisplayInfo[] {
    const trimmed = output.trim();
    if (!trimmed) {
        return [];
    }

    const parsed = JSON.parse(trimmed);
    if (Array.isArray(parsed)) {
        const appHosts = parsed
            .filter(isLsCandidate)
            .map(candidate => toDisplayCandidate(candidate));

        const unexpectedCandidateCount = parsed.length - appHosts.length;
        if (unexpectedCandidateCount > 0) {
            extensionLogOutputChannel.warn(`AppHost discovery returned ${unexpectedCandidateCount} candidate(s) with an unexpected shape; ignoring those entries.`);
        }

        return appHosts;
    }

    if (isAppHostProjectSearchResult(parsed)) {
        return parsed.app_host_candidates.map(candidate => ({
            ...toDisplayCandidate(candidate),
            selected: typeof parsed.selected_project_file === 'string' && isSamePath(parsed.selected_project_file, candidate.path),
        }));
    }

    if (isLegacyAppHostProjectSearchResult(parsed)) {
        return toCandidatesFromLegacySearchResult(parsed);
    }

    throw new Error('AppHost discovery returned an unexpected output shape.');
}

export function parseLegacyGetAppHostsOutput(output: string): LegacyAppHostProjectSearchResult {
    // `aspire extension get-apphosts` prints a single JSON object:
    //   {"selected_project_file":"/repo/AppHost/AppHost.csproj","all_project_file_candidates":["/repo/AppHost/AppHost.csproj"]}
    // Older builds can include log lines, so scan for the first line with the expected shape.
    for (const line of output.split(/\r?\n/)) {
        try {
            const parsed = JSON.parse(line);
            if (isLegacyAppHostProjectSearchResult(parsed)) {
                return parsed;
            }
        }
        catch {
        }
    }

    const parsed = JSON.parse(output.trim());
    if (isLegacyAppHostProjectSearchResult(parsed)) {
        return parsed;
    }

    throw new Error('aspire extension get-apphosts returned an unexpected output shape.');
}

export function toCandidatesFromLegacySearchResult(parsed: LegacyAppHostProjectSearchResult): CandidateAppHostDisplayInfo[] {
    return parsed.all_project_file_candidates.filter(candidate => typeof candidate === 'string').map(candidatePath => ({
        path: candidatePath,
        language: 'csharp',
        status: 'buildable',
    }));
}

function isLsCandidate(obj: unknown): obj is CandidateAppHostDisplayInfo {
    return !!obj
        && typeof obj === 'object'
        && typeof (obj as CandidateAppHostDisplayInfo).path === 'string'
        && typeof (obj as CandidateAppHostDisplayInfo).language === 'string'
        && typeof (obj as CandidateAppHostDisplayInfo).status === 'string';
}

function toDisplayCandidate(candidate: CandidateAppHostDisplayInfo | AppHostCandidate): CandidateAppHostDisplayInfo {
    const displayCandidate: CandidateAppHostDisplayInfo = {
        path: candidate.path,
        language: candidate.language,
        status: candidate.status,
    };

    const selected = 'selected' in candidate ? candidate.selected : undefined;
    if (selected !== undefined) {
        displayCandidate.selected = selected;
    }

    return displayCandidate;
}

function isLegacyAppHostProjectSearchResult(obj: unknown): obj is LegacyAppHostProjectSearchResult {
    return !!obj
        && typeof obj === 'object'
        && (typeof (obj as LegacyAppHostProjectSearchResult).selected_project_file === 'string' || (obj as LegacyAppHostProjectSearchResult).selected_project_file === null)
        && Array.isArray((obj as LegacyAppHostProjectSearchResult).all_project_file_candidates);
}

function isAppHostProjectSearchResult(obj: unknown): obj is AppHostProjectSearchResult {
    return !!obj
        && typeof obj === 'object'
        && (typeof (obj as AppHostProjectSearchResult).selected_project_file === 'string' || (obj as AppHostProjectSearchResult).selected_project_file === null)
        && Array.isArray((obj as AppHostProjectSearchResult).app_host_candidates)
        && (obj as AppHostProjectSearchResult).app_host_candidates.every(candidate =>
            candidate
            && typeof candidate.relativePath === 'string'
            && typeof candidate.path === 'string'
            && typeof candidate.language === 'string'
            && typeof candidate.status === 'string');
}
