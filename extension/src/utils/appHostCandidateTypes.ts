// Mirrors the `aspire ls --format json` candidate shape documented in
// docs/specs/cli-output-formats.md. Older CLI fallback results are adapted into
// this shape so extension code can keep using the modern discovery contract.
export interface CandidateAppHostDisplayInfo {
    path: string;
    language: string | null;
    status: string;
    selected?: boolean;
}

export interface AppHostCandidate {
    relativePath: string;
    path: string;
    language: string;
    status: string;
}

export interface AppHostProjectSearchResult {
    selected_project_file: string | null;
    all_project_file_candidates: string[];
    app_host_candidates: AppHostCandidate[];
}

export interface LegacyAppHostProjectSearchResult {
    selected_project_file: string | null;
    all_project_file_candidates: string[];
}

// Best-effort notification for candidates discovered before the final result is available.
// Buffered discovery does not invoke this callback; the returned promise remains authoritative.
export type IncrementalCandidateCallback = (candidate: CandidateAppHostDisplayInfo) => void;
