import * as path from 'path';
import * as vscode from 'vscode';
import * as strings from '../loc/strings';
import {
    CliIdentityInfo,
    CliUpdateRecommendation,
    CliVersionInfo,
    compareCliVersionValues,
    ConfigInfoProvider,
} from './configInfoProvider';
import { CliPathResolutionTarget } from './cliPathVariables';
import { getComparisonKey } from './paths/comparison';

const updateAspireCliCommand = 'aspire-vscode.updateSelf';
const versionRefreshIntervalMs = 5 * 60 * 1_000;
const versionFailureRetryMs = 60 * 1_000;
const completedUpdateRefreshIntervalMs = 6 * 60 * 60 * 1_000;
const unavailableRetryBaseMs = 60 * 1_000;
const unavailableRetryMaximumMs = 30 * 60 * 1_000;
const maximumUnavailableAttemptsPerIdentity = 3;
const persistedSuppressionKeyPrefix = 'outdatedCliNotification.suppressedCliVersion.';
const maximumPersistedSuppressions = 100;

type CliVersionProvider = Pick<ConfigInfoProvider, 'getCliIdentity' | 'getCliVersion' | 'getCliUpdateRecommendation'>;

export interface OutdatedCliNotificationSurface {
    showWarning(message: string, ...actions: string[]): Thenable<string | undefined>;
    executeCommand(command: string, ...args: unknown[]): Thenable<unknown>;
}

interface CliCheckState {
    identity: CliIdentityInfo | undefined;
    versionValidUntil: number;
    updateStatus: 'complete' | 'ineligible' | 'suppressed' | 'unavailable' | undefined;
    updateValidUntil: number;
    failureCount: number;
}

interface PendingNotification {
    target: CliPathResolutionTarget;
    cli: CliIdentityInfo;
    recommendedVersion: string;
}

interface PersistedSuppression {
    notificationKey: string;
    storageKey: string;
    suppressedAt: number;
}

const defaultSurface: OutdatedCliNotificationSurface = {
    showWarning: (message, ...actions) => vscode.window.showWarningMessage(message, ...actions),
    executeCommand: (command, ...args) => vscode.commands.executeCommand(command, ...args),
};

/**
 * Checks actively used Aspire CLIs for a same-channel update. Version sampling is cheap and
 * periodic; the heavyweight doctor adapter is limited to one active probe and cached independently.
 */
export class OutdatedCliNotifier implements vscode.Disposable {
    private readonly _stateByCliPath = new Map<string, CliCheckState>();
    private readonly _notifiedCliVersions = new Set<string>();
    private readonly _persistentlySuppressedCliVersions: Set<string>;
    private readonly _inFlightByCliPath = new Map<string, Promise<PendingNotification | undefined>>();
    private readonly _cancellationSource = new vscode.CancellationTokenSource();
    private readonly _versionLimiter = new AsyncLimiter(4);
    private readonly _doctorLimiter = new AsyncLimiter(1);
    private _disposed = false;

    constructor(
        private readonly _versionProvider: CliVersionProvider,
        private readonly _surface: OutdatedCliNotificationSurface = defaultSurface,
        private readonly _now: () => number = Date.now,
        private readonly _globalState?: vscode.Memento,
    ) {
        this._persistentlySuppressedCliVersions = new Set(
            readPersistedSuppressions(_globalState).map(suppression => suppression.notificationKey));
    }

    async notifyIfOutdated(target: CliPathResolutionTarget, cliPath: string): Promise<void> {
        if (this._disposed) {
            return;
        }

        const cliPathKey = getComparisonKey(path.normalize(cliPath));
        if ((this._stateByCliPath.get(cliPathKey)?.versionValidUntil ?? 0) > this._now()) {
            return;
        }

        const existingProbe = this._inFlightByCliPath.get(cliPathKey);
        if (existingProbe) {
            await existingProbe;
            return;
        }

        const checkStartedAt = this._now();
        let probe!: Promise<PendingNotification | undefined>;
        probe = this._checkForUpdate(target, cliPathKey, cliPath, checkStartedAt).finally(() => {
            if (this._inFlightByCliPath.get(cliPathKey) === probe) {
                this._inFlightByCliPath.delete(cliPathKey);
            }
        });
        this._inFlightByCliPath.set(cliPathKey, probe);
        const notification = await probe;
        if (this._disposed || !notification) {
            return;
        }

        const notificationKey = getNotificationKey(notification.cli.cliPath, notification.cli.version);
        if (this._notifiedCliVersions.has(notificationKey) ||
            this._persistentlySuppressedCliVersions.has(notificationKey)) {
            return;
        }

        this._notifiedCliVersions.add(notificationKey);
        const selection = await this._surface.showWarning(
            strings.outdatedAspireCliWarning(
                notification.cli.version,
                notification.cli.cliPath,
                notification.recommendedVersion),
            strings.updateAspireCliAction,
            strings.dontShowAgainLabel);
        if (this._disposed) {
            return;
        }
        if (selection === strings.dontShowAgainLabel) {
            await this._suppressNotification(notificationKey, notification.cli.cliPath);
            return;
        }
        if (selection !== strings.updateAspireCliAction) {
            return;
        }

        // This user-initiated guard intentionally bypasses the five-minute cache.
        const currentVersionProbe = await this._versionLimiter.run(() =>
            this._versionProvider.getCliVersion({
                target: notification.target,
                cliPath: notification.cli.cliPath,
                cancellationToken: this._cancellationSource.token,
            }));
        if (!currentVersionProbe.executed || this._disposed) {
            return;
        }
        if (!currentVersionProbe.value || currentVersionProbe.value.version !== notification.cli.version) {
            return;
        }

        await this._surface.executeCommand(updateAspireCliCommand, notification.target, notification.cli.cliPath);
    }

    private async _checkForUpdate(
        target: CliPathResolutionTarget,
        cliPathKey: string,
        cliPath: string,
        checkStartedAt: number,
    ): Promise<PendingNotification | undefined> {
        const versionProbe = await this._versionLimiter.run(() =>
            this._versionProvider.getCliIdentity({
                target,
                cliPath,
                cancellationToken: this._cancellationSource.token,
            }));
        if (!versionProbe.executed || this._disposed) {
            return undefined;
        }

        const now = this._now();
        const previous = this._stateByCliPath.get(cliPathKey);
        const identity = versionProbe.value;
        if (!identity) {
            this._stateByCliPath.set(cliPathKey, {
                identity: previous?.identity,
                versionValidUntil: checkStartedAt + versionFailureRetryMs,
                updateStatus: previous?.updateStatus,
                updateValidUntil: previous?.updateValidUntil ?? 0,
                failureCount: previous?.failureCount ?? 0,
            });
            return undefined;
        }

        const identityChanged = !areCliIdentitiesEqual(previous?.identity, identity);
        const state: CliCheckState = identityChanged
            ? {
                identity,
                versionValidUntil: checkStartedAt + versionRefreshIntervalMs,
                updateStatus: undefined,
                updateValidUntil: 0,
                failureCount: 0,
            }
            : {
                identity,
                versionValidUntil: checkStartedAt + versionRefreshIntervalMs,
                updateStatus: previous?.updateStatus,
                updateValidUntil: previous?.updateValidUntil ?? 0,
                failureCount: previous?.failureCount ?? 0,
            };
        this._stateByCliPath.set(cliPathKey, state);

        const notificationKey = getNotificationKey(identity.cliPath, identity.version);
        if (this._persistentlySuppressedCliVersions.has(notificationKey)) {
            state.updateStatus = 'suppressed';
            state.updateValidUntil = Number.POSITIVE_INFINITY;
            state.failureCount = 0;
            return undefined;
        }

        if (!identityChanged &&
            (state.updateStatus === 'ineligible' ||
                state.updateStatus === 'suppressed' ||
                (state.updateStatus !== undefined && state.updateValidUntil > now))) {
            return undefined;
        }

        const serializedRecommendationProbe = await this._doctorLimiter.run(() =>
            this._versionLimiter.run(() =>
                this._versionProvider.getCliUpdateRecommendation({
                    target,
                    cliPath,
                    identityChannelOverride: identity.identityChannelOverride,
                    cancellationToken: this._cancellationSource.token,
                })));
        const recommendationProbe = serializedRecommendationProbe.executed
            ? serializedRecommendationProbe.value
            : undefined;
        if (!recommendationProbe?.executed || this._disposed) {
            return undefined;
        }

        const recommendation = recommendationProbe.value;
        if (recommendation.status === 'ineligible') {
            state.updateStatus = 'ineligible';
            state.updateValidUntil = Number.POSITIVE_INFINITY;
            state.failureCount = 0;
            return undefined;
        }
        if (recommendation.status === 'unavailable' ||
            compareCliVersionValues(identity.version, recommendation.currentVersion) !== 0) {
            this._recordUnavailable(state);
            return undefined;
        }

        state.updateStatus = 'complete';
        state.updateValidUntil = this._now() + completedUpdateRefreshIntervalMs;
        state.failureCount = 0;
        if (recommendation.status !== 'available') {
            return undefined;
        }

        const comparison = compareCliVersionValues(identity.version, recommendation.version);
        return comparison !== undefined && comparison < 0
            ? { target, cli: identity, recommendedVersion: recommendation.version }
            : undefined;
    }

    private _recordUnavailable(state: CliCheckState): void {
        state.failureCount = state.updateStatus === 'unavailable' ? state.failureCount + 1 : 1;
        state.updateStatus = 'unavailable';
        if (state.failureCount >= maximumUnavailableAttemptsPerIdentity) {
            // Doctor runs the full environment-check battery. Stop retrying an unchanged identity
            // for this session after a few silent failures; five-minute version sampling continues,
            // so replacing the CLI resets the state and permits a fresh update check.
            state.updateValidUntil = Number.POSITIVE_INFINITY;
            return;
        }
        state.updateValidUntil = this._now() + Math.min(
            unavailableRetryBaseMs * 2 ** (state.failureCount - 1),
            unavailableRetryMaximumMs);
    }

    private async _suppressNotification(notificationKey: string, cliPath: string): Promise<void> {
        this._persistentlySuppressedCliVersions.add(notificationKey);

        const cliPathKey = getComparisonKey(path.normalize(cliPath));
        const state = this._stateByCliPath.get(cliPathKey);
        if (state?.identity &&
            getNotificationKey(state.identity.cliPath, state.identity.version) === notificationKey) {
            state.updateStatus = 'suppressed';
            state.updateValidUntil = Number.POSITIVE_INFINITY;
            state.failureCount = 0;
        }

        if (!this._globalState) {
            return;
        }

        // Each VS Code window owns a notifier, while globalState is shared by the profile. Store
        // suppressions under independent keys so concurrent choices cannot overwrite one another.
        const suppressedAt = this._now();
        await this._globalState.update(getPersistedSuppressionStorageKey(notificationKey), suppressedAt);
        const persistedSuppressions = readPersistedSuppressions(this._globalState);
        for (const persistedSuppression of persistedSuppressions) {
            this._persistentlySuppressedCliVersions.add(persistedSuppression.notificationKey);
        }

        const excessCount = persistedSuppressions.length - maximumPersistedSuppressions;
        if (excessCount <= 0) {
            return;
        }

        const suppressionsToRemove = persistedSuppressions
            .filter(suppression => suppression.notificationKey !== notificationKey)
            .sort((left, right) =>
                left.suppressedAt - right.suppressedAt ||
                left.storageKey.localeCompare(right.storageKey))
            .slice(0, excessCount);
        for (const suppression of suppressionsToRemove) {
            await this._globalState.update(suppression.storageKey, undefined);
            this._persistentlySuppressedCliVersions.delete(suppression.notificationKey);
        }
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        this._cancellationSource.cancel();
        this._cancellationSource.dispose();
        this._doctorLimiter.dispose();
        this._versionLimiter.dispose();
        this._inFlightByCliPath.clear();
        this._stateByCliPath.clear();
        this._notifiedCliVersions.clear();
    }
}

function getNotificationKey(cliPath: string, version: string): string {
    return `${getComparisonKey(path.normalize(cliPath))}\u0000${version}`;
}

function getPersistedSuppressionStorageKey(notificationKey: string): string {
    return `${persistedSuppressionKeyPrefix}${encodeURIComponent(notificationKey)}`;
}

function readPersistedSuppressions(globalState: vscode.Memento | undefined): PersistedSuppression[] {
    if (!globalState) {
        return [];
    }

    const suppressions: PersistedSuppression[] = [];
    for (const storageKey of globalState.keys()) {
        if (!storageKey.startsWith(persistedSuppressionKeyPrefix)) {
            continue;
        }

        const suppressedAt = globalState.get<unknown>(storageKey);
        if (typeof suppressedAt !== 'number' || !Number.isFinite(suppressedAt)) {
            continue;
        }

        try {
            suppressions.push({
                notificationKey: decodeURIComponent(storageKey.slice(persistedSuppressionKeyPrefix.length)),
                storageKey,
                suppressedAt,
            });
        }
        catch {
            // Ignore unrelated or corrupted global state keys rather than blocking extension startup.
        }
    }

    return suppressions;
}

function areCliIdentitiesEqual(left: CliIdentityInfo | undefined, right: CliIdentityInfo): boolean {
    return left?.version === right.version &&
        left.identityChannelOverride === right.identityChannelOverride;
}

type LimiterResult<T> =
    | { executed: true; value: T }
    | { executed: false };

class AsyncLimiter implements vscode.Disposable {
    private readonly _waiters: Array<(acquired: boolean) => void> = [];
    private _activeCount = 0;
    private _disposed = false;

    constructor(private readonly _maximumConcurrency: number) {
    }

    async run<T>(action: () => Promise<T>): Promise<LimiterResult<T>> {
        if (!await this._acquire()) {
            return { executed: false };
        }
        if (this._disposed) {
            this._release();
            return { executed: false };
        }

        try {
            return { executed: true, value: await action() };
        }
        finally {
            this._release();
        }
    }

    private async _acquire(): Promise<boolean> {
        if (this._disposed) {
            return false;
        }
        if (this._activeCount < this._maximumConcurrency) {
            this._activeCount++;
            return true;
        }

        return await new Promise<boolean>(resolve => this._waiters.push(resolve));
    }

    private _release(): void {
        const waiter = this._waiters.shift();
        if (waiter) {
            waiter(true);
            return;
        }

        this._activeCount--;
    }

    dispose(): void {
        this._disposed = true;
        while (this._waiters.length > 0) {
            this._waiters.shift()?.(false);
        }
    }
}
