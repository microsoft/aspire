import * as path from 'path';
import * as vscode from 'vscode';

import { canonicalizeAppHostPath } from '../utils/appHostIdentity';
import { extensionLogOutputChannel } from '../utils/logging';
import { isCommandCancellation } from '../utils/telemetry';
import {
    type AppHostTarget,
    type AppHostTargetDiscoveryService,
    type AppHostTargetResolution,
    type AppHostTargetResolver,
} from './appHostTargetResolverContracts';

/**
 * Upper bound on the workspace-relative path a confirmation may show.
 *
 * A path longer than this is refused outright rather than elided, because an elided path
 * no longer identifies one file: two AppHosts sharing a long prefix would produce the same
 * prompt. The bound is far above any realistic repository path (Windows' own MAX_PATH is
 * 260 for a full path), so refusing beyond it costs nothing in practice.
 */
const maxConfirmationPathLength = 512;

/** Reject model-supplied selectors large enough to make normalization itself expensive. */
const maxAppHostSelectorLength = 4096;

/** Cap on how many AppHost paths an `unknownAppHost` result lists back to the model. */
const maxReportedKnownAppHosts = 32;

/**
 * Characters that change what a path *is* without changing, or while changing, how it
 * looks: C0/C1 controls and DEL, line and paragraph separators, plus every Unicode format
 * character (`\p{Cf}`).
 *
 * Bidi controls (U+202A-U+202E, U+2066-U+2069) reorder the run that follows them, so a
 * path can render as a completely different one. Zero-width characters (U+200B-U+200D)
 * are invisible, so two distinct files can produce identical-looking prompts. U+2028 and
 * U+2029 can create a new rendered line or paragraph in Markdown confirmations. A registry
 * entry carrying any of these is dropped rather than shown with the characters deleted,
 * because deleting them would break the one-to-one relationship between the identity the
 * user confirms and the file that runs.
 * See https://unicode.org/reports/tr9/ and https://unicode.org/reports/tr36/#Bidirectional_Text_Spoofing
 */
const identityChangingCharacters = /[\u0000-\u001F\u007F-\u009F\u2028\u2029]|\p{Cf}/u;
const confirmationBreakingCharacters = /[\u2028\u2029]/u;

export interface AppHostTargetResolverServiceDependencies {
    readonly discoveryService: AppHostTargetDiscoveryService;
}

/**
 * Resolves a model selector only against the editor's discovered AppHost registry.
 *
 * Consumers must not recreate discovery, containment, or multi-root selection logic.
 */
export class AppHostTargetResolverService implements AppHostTargetResolver {
    constructor(private readonly _dependencies: AppHostTargetResolverServiceDependencies) {
    }

    /**
     * The selector is only ever compared against entries the discovery service enumerated;
     * it is never joined onto a directory, normalized into a path, or passed to the
     * filesystem. A resolved target therefore always comes from Aspire's own registry.
     */
    async resolveTarget(rawAppHost: unknown, token: vscode.CancellationToken): Promise<AppHostTargetResolution> {
        if (typeof rawAppHost !== 'string') {
            return { resolved: false, outcome: 'invalidInput' };
        }

        const selector = rawAppHost.trim();
        if (selector.length === 0 ||
            selector.length > maxAppHostSelectorLength ||
            confirmationBreakingCharacters.test(selector) ||
            path.isAbsolute(selector)) {
            return { resolved: false, outcome: 'invalidInput' };
        }

        let knownAppHosts: readonly AppHostTarget[];
        try {
            knownAppHosts = await this._enumerateKnownAppHosts(token);
        }
        catch (error) {
            if (isCommandCancellation(error) || token.isCancellationRequested) {
                return { resolved: false, outcome: 'cancelled' };
            }

            // Discovery errors can contain CLI and filesystem detail. The tool result carries
            // only the bounded outcome while the extension log retains diagnostics.
            extensionLogOutputChannel.warn(`Aspire language model tools could not enumerate AppHosts: ${String(error)}`);
            return { resolved: false, outcome: 'discoveryFailed' };
        }

        if (token.isCancellationRequested) {
            return { resolved: false, outcome: 'cancelled' };
        }

        const requestedKey = toSelectorKey(selector);
        const displayMatches = knownAppHosts.filter(candidate => toSelectorKey(candidate.displayPath) === requestedKey);
        if ((vscode.workspace.workspaceFolders?.length ?? 0) > 1) {
            // A bare relative selector is not stable in a multi-root workspace: a confirmation
            // could name the only current match under root A, then a later invocation could
            // re-resolve the same text under root B. Require the folder-qualified identity
            // the confirmation displays so each invocation is independently bound to one root.
            if (displayMatches.length === 1) {
                return { resolved: true, target: displayMatches[0] };
            }

            if (displayMatches.length > 1) {
                return {
                    resolved: false,
                    outcome: 'ambiguousAppHost',
                    knownAppHosts: describeKnownAppHosts(displayMatches),
                };
            }

            const relativeMatches = knownAppHosts.filter(candidate => toSelectorKey(candidate.relativePath) === requestedKey);
            if (relativeMatches.length > 0) {
                return {
                    resolved: false,
                    outcome: 'ambiguousAppHost',
                    knownAppHosts: describeKnownAppHosts(relativeMatches),
                };
            }

            return {
                resolved: false,
                outcome: 'unknownAppHost',
                knownAppHosts: describeKnownAppHosts(knownAppHosts),
            };
        }

        const matches = knownAppHosts.filter(candidate =>
            toSelectorKey(candidate.relativePath) === requestedKey ||
            toSelectorKey(candidate.displayPath) === requestedKey);
        if (matches.length === 0) {
            return {
                resolved: false,
                outcome: 'unknownAppHost',
                knownAppHosts: describeKnownAppHosts(knownAppHosts),
            };
        }

        if (matches.length > 1) {
            return {
                resolved: false,
                outcome: 'ambiguousAppHost',
                knownAppHosts: describeKnownAppHosts(matches),
            };
        }

        return { resolved: true, target: matches[0] };
    }

    private async _enumerateKnownAppHosts(token: vscode.CancellationToken): Promise<readonly AppHostTarget[]> {
        const workspaceFolders = vscode.workspace.workspaceFolders ?? [];
        const candidatesByFolder = await Promise.all(workspaceFolders.map(async folder => ({
            folder,
            candidates: await this._dependencies.discoveryService.discover(folder, false, token),
        })));

        const targets = new Map<string, AppHostTarget>();
        for (const { folder, candidates } of candidatesByFolder) {
            // Containment is decided on the real paths, because a link inside the workspace
            // can point at a file outside it. The confirmation would show the in-workspace
            // link while `startDebugging` executed the external target, so a lexical check
            // alone would let the workspace boundary be crossed under an in-workspace name.
            const canonicalFolderPath = canonicalizeAppHostPath(folder.uri.fsPath);
            for (const candidate of candidates) {
                const relativePath = toContainedPosixRelativePath(folder.uri.fsPath, candidate.path);
                if (relativePath === undefined) {
                    continue;
                }

                // The lexical relative path is still what gets displayed: it is the name the
                // caller sees in the explorer, and it is the one they can pass back.
                if (toContainedPosixRelativePath(canonicalFolderPath, canonicalizeAppHostPath(candidate.path)) === undefined) {
                    continue;
                }

                const displayPath = workspaceFolders.length > 1
                    ? `${folder.name}/${relativePath}`
                    : relativePath;
                // Nested workspace folders enumerate the same file twice. Keying by the
                // absolute path collapses those into one target so a selector matching both
                // is not reported as ambiguous against itself. The deepest folder wins, so
                // the displayed path matches the folder the user sees in the explorer.
                const key = toSelectorKey(candidate.path);
                const existing = targets.get(key);
                if (existing && existing.relativePath.length <= relativePath.length) {
                    continue;
                }

                targets.set(key, {
                    absolutePath: candidate.path,
                    relativePath,
                    displayPath,
                });
            }
        }

        return [...targets.values()].filter(target =>
            !identityChangingCharacters.test(target.displayPath) &&
            target.displayPath.length <= maxConfirmationPathLength);
    }
}

/**
 * Normalizes a selector or registry path into the key both sides are compared on.
 *
 * The comparison is deliberately narrow: a leading `./` is dropped because it is noise,
 * and Windows separators and casing are normalized to match that filesystem. On POSIX a
 * backslash is a valid filename character, so treating it as a separator would alias two
 * different registry entries. Nothing else is normalized. `..` segments, for instance,
 * are left alone precisely so they can never match anything the registry enumerated.
 */
function toSelectorKey(value: string): string {
    if (process.platform === 'win32') {
        return value.replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase();
    }

    return value.replace(/^\.\//, '');
}

/**
 * Renders the selectors a failed resolution can offer back to the model.
 *
 * The list is capped because a large monorepo can enumerate hundreds of AppHosts and the
 * result is spent from the model's context window.
 */
function describeKnownAppHosts(targets: readonly AppHostTarget[]): readonly string[] {
    return targets.slice(0, maxReportedKnownAppHosts).map(target => target.displayPath);
}

/**
 * Path relative to `folderPath` with `/` separators, or `undefined` when `candidate`
 * is not inside the folder.
 */
function toContainedPosixRelativePath(folderPath: string, candidate: string): string | undefined {
    const relative = path.relative(folderPath, candidate);
    if (relative.length === 0 || relative.startsWith('..') || path.isAbsolute(relative)) {
        return undefined;
    }

    return relative.split(path.sep).join('/');
}
