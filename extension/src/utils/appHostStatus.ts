import * as vscode from 'vscode';
import type { AppHostCandidate } from '../types/appHostCandidate';

/**
 * AppHost candidate statuses emitted by `aspire ls`. The CLI owns this vocabulary
 * (see `AppHostProjectCandidateStatus` in src/Aspire.Cli/Commands/LsCommand.cs), so the wire
 * field `AppHostCandidate.status` stays a `string`. This module is the single place that
 * interprets that vocabulary, keeping the buildable rule, the user-facing label, and the tree
 * icon from drifting apart.
 */
export type AppHostCandidateStatus = 'buildable' | 'possibly-buildable' | 'possibly-unbuildable';

// The statuses we treat as launchable. `possibly-buildable` is included because the CLI cannot
// always confirm buildability without a design-time evaluation, which we intentionally skip in
// large workspaces.
const buildableStatuses: readonly AppHostCandidateStatus[] = ['buildable', 'possibly-buildable'];

/**
 * Whether a candidate should be treated as launchable in the workspace view.
 */
export function isBuildableAppHostCandidate(candidate: AppHostCandidate): boolean {
    return buildableStatuses.includes(candidate.status as AppHostCandidateStatus);
}

/**
 * Converts a kebab-case CLI status identifier (for example, "possibly-buildable") into a
 * user-facing label ("Possibly Buildable"). Tolerates unknown values so a newer CLI status
 * still renders sensibly.
 */
export function formatAppHostCandidateStatusLabel(status: string): string {
    return status
        .split(/[-_\s]+/)
        .filter(part => part.length > 0)
        .map(part => `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
        .join(' ');
}

/**
 * Maps a candidate status to its tree icon. `buildable` and `possibly-buildable` get distinct
 * icons; every other value — including `possibly-unbuildable` and any status a newer CLI might
 * add — falls back to a neutral icon rather than throwing, so unexpected input cannot break tree
 * rendering.
 */
export function appHostCandidateStatusIcon(status: string): vscode.ThemeIcon {
    switch (status) {
        case 'buildable':
            return new vscode.ThemeIcon('pass', new vscode.ThemeColor('testing.iconPassed'));
        case 'possibly-buildable':
            return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
        default:
            return new vscode.ThemeIcon('question');
    }
}
