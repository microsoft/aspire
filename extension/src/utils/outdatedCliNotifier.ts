import * as path from 'path';
import * as vscode from 'vscode';
import * as strings from '../loc/strings';
import {
    CliUpdateRecommendation,
    CliVersionInfo,
    compareCliVersionValues,
    ConfigInfoProvider,
    resolveConfigInfoWorkingDirectory,
} from './configInfoProvider';
import { CliPathResolutionTarget, getCliPathTargetKey } from './cliPathVariables';
import { extensionLogOutputChannel } from './logging';
import { OutdatedCliNotificationClaim, OutdatedCliSuppressionStore } from './outdatedCliSuppressionStore';
import { getComparisonKey } from './paths/comparison';

const updateAspireCliCommand = 'aspire-vscode.updateSelf';
const versionRefreshIntervalMs = 5 * 60 * 1_000;
const versionFailureRetryMs = 60 * 1_000;
const completedUpdateRefreshIntervalMs = 6 * 60 * 60 * 1_000;
const unavailableRetryBaseMs = 60 * 1_000;
const unavailableRetryMaximumMs = 30 * 60 * 1_000;
const maximumUnavailableAttemptsPerIdentity = 3;

type CliVersionProvider = Pick<ConfigInfoProvider, 'getCliVersion' | 'getCliUpdateRecommendation'>;

export interface OutdatedCliNotificationSurface {
    showWarning(message: string, ...actions: string[]): Thenable<string | undefined>;
    executeCommand(command: string, ...args: unknown[]): Thenable<unknown>;
}

interface CliCheckState {
    identity: CliVersionInfo | undefined;
    versionValidUntil: number;
    updateStatus: 'complete' | 'ineligible' | 'unavailable' | undefined;
    updateValidUntil: number;
    failureCount: number;
}

interface PendingNotification {
    target: CliPathResolutionTarget;
    cli: CliVersionInfo;
    recommendedVersion: string;
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
    private readonly _stateByCheckKey = new Map<string, CliCheckState>();
    private readonly _notifiedCliVersions = new Set<string>();
    private readonly _persistentlySuppressedCliVersions: Set<string>;
    private readonly _inFlightByCheckKey = new Map<string, Promise<PendingNotification | undefined>>();
    private readonly _inFlightVersionByCliPath = new Map<string, Promise<CliVersionInfo | null | undefined>>();
    private readonly _cancellationSource = new vscode.CancellationTokenSource();
    private readonly _versionQueue = new AsyncSerialQueue();
    private readonly _doctorQueue = new AsyncSerialQueue();
    private _disposed = false;

    constructor(
        private readonly _versionProvider: CliVersionProvider,
        private readonly _surface: OutdatedCliNotificationSurface = defaultSurface,
        private readonly _now: () => number = Date.now,
        private readonly _suppressionStore?: OutdatedCliSuppressionStore,
    ) {
        this._persistentlySuppressedCliVersions = new Set();
    }

    async notifyIfOutdated(target: CliPathResolutionTarget, cliPath: string): Promise<void> {
        if (this._disposed) {
            return;
        }

        const workingDirectory = resolveConfigInfoWorkingDirectory(target);
        const checkKey = getCliCheckKey(target, cliPath, workingDirectory);
        if ((this._stateByCheckKey.get(checkKey)?.versionValidUntil ?? 0) > this._now()) {
            return;
        }
        if (!await this._refreshPersistedSuppressions() || this._disposed) {
            return;
        }
        const existingProbe = this._inFlightByCheckKey.get(checkKey);
        if (existingProbe) {
            await existingProbe;
            return;
        }

        const checkStartedAt = this._now();
        let probe!: Promise<PendingNotification | undefined>;
        probe = this._checkForUpdate(
            target,
            checkKey,
            cliPath,
            workingDirectory,
            checkStartedAt).finally(() => {
            if (this._inFlightByCheckKey.get(checkKey) === probe) {
                this._inFlightByCheckKey.delete(checkKey);
            }
        });
        this._inFlightByCheckKey.set(checkKey, probe);
        const notification = await probe;
        if (this._disposed || !notification) {
            return;
        }

        const notificationKey = getNotificationKey(notification.cli.cliPath, notification.cli.version);
        const sessionNotificationKey = getSessionNotificationKey(notification.cli);
        if (this._notifiedCliVersions.has(sessionNotificationKey) ||
            this._persistentlySuppressedCliVersions.has(notificationKey)) {
            return;
        }
        this._notifiedCliVersions.add(sessionNotificationKey);

        let claim: OutdatedCliNotificationClaim | undefined;
        if (this._suppressionStore) {
            try {
                claim = await this._suppressionStore.tryClaimNotification(notificationKey);
            }
            catch (error) {
                this._notifiedCliVersions.delete(sessionNotificationKey);
                extensionLogOutputChannel.warn(`Unable to claim Aspire CLI update notification: ${String(error)}`);
                this._invalidateRecommendationAfterSuppressionFailure(checkKey, notification.cli);
                return;
            }
            if (!claim) {
                this._persistentlySuppressedCliVersions.add(notificationKey);
                return;
            }
            try {
                if (!claim.isValid()) {
                    this._notifiedCliVersions.delete(sessionNotificationKey);
                    this._invalidateRecommendationAfterSuppressionFailure(checkKey, notification.cli);
                    await this._releaseNotificationClaim(claim);
                    return;
                }
            }
            catch (error) {
                this._notifiedCliVersions.delete(sessionNotificationKey);
                extensionLogOutputChannel.warn(`Unable to validate Aspire CLI update notification claim: ${String(error)}`);
                this._invalidateRecommendationAfterSuppressionFailure(checkKey, notification.cli);
                await this._releaseNotificationClaim(claim);
                return;
            }
        }
        if (this._disposed) {
            await this._releaseNotificationClaim(claim);
            return;
        }

        let selectionPromise: Thenable<string | undefined>;
        try {
            selectionPromise = this._surface.showWarning(
                strings.outdatedAspireCliWarning(
                    notification.cli.version,
                    notification.cli.cliPath,
                    notification.recommendedVersion),
                strings.updateAspireCliAction,
                strings.dontShowAgainLabel);
        }
        finally {
            await this._releaseNotificationClaim(claim);
        }
        const selection = await selectionPromise;
        if (this._disposed) {
            return;
        }
        if (selection === strings.dontShowAgainLabel) {
            await this._suppressNotification(notificationKey);
            return;
        }
        if (selection !== strings.updateAspireCliAction) {
            return;
        }

        // This user-initiated guard intentionally bypasses the five-minute cache.
        const currentVersionProbe = await this._getCliVersion(
            notification.target,
            notification.cli.cliPath,
            false);
        if (this._disposed) {
            return;
        }
        if (!currentVersionProbe || !areCliIdentitiesEqual(currentVersionProbe, notification.cli)) {
            return;
        }

        await this._surface.executeCommand(updateAspireCliCommand, notification.target, notification.cli.cliPath);
    }

    private async _refreshPersistedSuppressions(): Promise<boolean> {
        if (!this._suppressionStore) {
            return true;
        }

        try {
            for (const notificationKey of await this._suppressionStore.readAll()) {
                this._persistentlySuppressedCliVersions.add(notificationKey);
            }
            return true;
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Unable to read Aspire CLI warning suppressions: ${String(error)}`);
            return false;
        }
    }

    private _invalidateRecommendationAfterSuppressionFailure(
        checkKey: string,
        identity: CliVersionInfo,
    ): void {
        const state = this._stateByCheckKey.get(checkKey);
        if (state?.identity && areCliIdentitiesEqual(state.identity, identity)) {
            state.versionValidUntil = 0;
            state.updateStatus = undefined;
            state.updateValidUntil = 0;
            state.failureCount = 0;
        }
    }

    private async _releaseNotificationClaim(claim: OutdatedCliNotificationClaim | undefined): Promise<void> {
        try {
            await claim?.release();
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Unable to release Aspire CLI update notification claim: ${String(error)}`);
        }
    }

    private _getCliVersion(
        target: CliPathResolutionTarget,
        cliPath: string,
        coalesce = true,
    ): Promise<CliVersionInfo | null | undefined> {
        const cliPathKey = getComparisonKey(path.normalize(cliPath));
        const startProbe = () => this._versionQueue.run(
            () => this._versionProvider.getCliVersion({
                target,
                cliPath,
                cancellationToken: this._cancellationSource.token,
            }),
            () => this._disposed);
        if (!coalesce) {
            return startProbe();
        }

        const existing = this._inFlightVersionByCliPath.get(cliPathKey);
        if (existing) {
            return existing;
        }

        let probe!: Promise<CliVersionInfo | null | undefined>;
        probe = startProbe()
            .finally(() => {
                if (this._inFlightVersionByCliPath.get(cliPathKey) === probe) {
                    this._inFlightVersionByCliPath.delete(cliPathKey);
                }
            });
        this._inFlightVersionByCliPath.set(cliPathKey, probe);
        return probe;
    }

    private async _checkForUpdate(
        target: CliPathResolutionTarget,
        checkKey: string,
        cliPath: string,
        workingDirectory: string,
        checkStartedAt: number,
    ): Promise<PendingNotification | undefined> {
        const versionProbe = await this._getCliVersion(target, cliPath);
        if (this._disposed) {
            return undefined;
        }

        const now = this._now();
        const previous = this._stateByCheckKey.get(checkKey);
        const identity = versionProbe;
        if (!identity) {
            this._stateByCheckKey.set(checkKey, {
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
        this._stateByCheckKey.set(checkKey, state);

        const notificationKey = getNotificationKey(identity.cliPath, identity.version);
        if (this._persistentlySuppressedCliVersions.has(notificationKey)) {
            return undefined;
        }

        if (!identityChanged &&
            (state.updateStatus === 'ineligible' ||
                (state.updateStatus !== undefined && state.updateValidUntil > now))) {
            return undefined;
        }

        const recommendation = await this._doctorQueue.run(
            () => this._versionProvider.getCliUpdateRecommendation({
                target,
                cliPath,
                workingDirectory,
                cancellationToken: this._cancellationSource.token,
            }),
            () => this._disposed);
        if (recommendation === undefined || this._disposed) {
            return undefined;
        }

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

    private async _suppressNotification(notificationKey: string): Promise<void> {
        this._persistentlySuppressedCliVersions.add(notificationKey);

        try {
            await this._suppressionStore?.add(notificationKey);
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Unable to persist Aspire CLI warning suppression: ${String(error)}`);
        }
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        this._cancellationSource.cancel();
        this._cancellationSource.dispose();
        this._inFlightByCheckKey.clear();
        this._inFlightVersionByCliPath.clear();
        this._stateByCheckKey.clear();
        this._notifiedCliVersions.clear();
    }
}

function getNotificationKey(cliPath: string, version: string): string {
    return `${getComparisonKey(path.normalize(cliPath))}\u0000${version}`;
}

function getSessionNotificationKey(cli: CliVersionInfo): string {
    return `${getNotificationKey(cli.cliPath, cli.version)}\u0000${cli.executableIdentity}`;
}

function getCliCheckKey(
    target: CliPathResolutionTarget,
    cliPath: string,
    workingDirectory: string,
): string {
    return `${getCliPathTargetKey(target)}\u0000${getComparisonKey(path.normalize(cliPath))}\u0000${getComparisonKey(path.normalize(workingDirectory))}`;
}

function areCliIdentitiesEqual(left: CliVersionInfo | undefined, right: CliVersionInfo): boolean {
    return left?.version === right.version &&
        left.executableIdentity === right.executableIdentity;
}

class AsyncSerialQueue {
    private _tail: Promise<void> = Promise.resolve();

    async run<T>(action: () => Promise<T>, isCancelled: () => boolean): Promise<T | undefined> {
        const previous = this._tail;
        let release!: () => void;
        this._tail = new Promise(resolve => release = resolve);
        await previous;

        try {
            return isCancelled() ? undefined : await action();
        }
        finally {
            release();
        }
    }
}
