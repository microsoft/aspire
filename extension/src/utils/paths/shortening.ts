import * as path from 'path';

const projectFileExtensions = new Set(['.csproj', '.fsproj', '.vbproj']);

export function shortenPath(filePath: string): string {
    return shortenPaths([filePath])[0] ?? filePath;
}

export function shortenPaths(filePaths: readonly string[]): string[] {
    const states: ShortenedPathState[] = [];
    const stateByPath = new Map<string, ShortenedPathState>();

    for (const filePath of filePaths) {
        const pathKey = filePath;
        let state = stateByPath.get(pathKey);
        if (!state) {
            state = createShortenedPathState(filePath);
            stateByPath.set(pathKey, state);
            states.push(state);
        }
    }

    while (true) {
        const duplicateLabels = new Set<string>();
        const seenLabels = new Set<string>();

        for (const state of states) {
            const labelKey = state.label;
            if (seenLabels.has(labelKey)) {
                duplicateLabels.add(labelKey);
            } else {
                seenLabels.add(labelKey);
            }
        }

        if (duplicateLabels.size === 0) {
            break;
        }

        for (const state of states) {
            if (duplicateLabels.has(state.label)) {
                expandShortenedPathState(state);
            }
        }
    }

    return filePaths.map(filePath => stateByPath.get(filePath)?.label ?? filePath);
}

interface ShortenedPathState {
    originalPath: string;
    segments: string[];
    depth: number;
    label: string;
}

function createShortenedPathState(filePath: string): ShortenedPathState {
    const normalized = filePath.replace(/\\/g, '/').replace(/\/+$/, '');
    const segments = normalized.split('/');
    const fileName = segments[segments.length - 1] || filePath;
    const extension = path.extname(fileName).toLowerCase();
    const isProjectFile = projectFileExtensions.has(extension);
    const depth = !isProjectFile && segments.length >= 2 ? 2 : 1;

    return {
        originalPath: filePath,
        segments,
        depth,
        label: depth >= 2 ? joinPathSegments(segments.slice(-depth)) : fileName,
    };
}

function expandShortenedPathState(state: ShortenedPathState): void {
    state.depth++;

    if (state.depth >= state.segments.length) {
        state.label = state.originalPath;
        return;
    }

    const firstCandidateIndex = state.segments.length - state.depth;
    const firstCandidateSegment = state.segments[firstCandidateIndex];
    if (firstCandidateSegment.length === 0 || isWindowsDriveSegment(firstCandidateSegment)) {
        state.label = state.originalPath;
        return;
    }

    state.label = joinPathSegments(state.segments.slice(firstCandidateIndex));
}

function joinPathSegments(segments: readonly string[]): string {
    return segments.join('/');
}

function isWindowsDriveSegment(segment: string): boolean {
    return /^[a-zA-Z]:$/.test(segment);
}
