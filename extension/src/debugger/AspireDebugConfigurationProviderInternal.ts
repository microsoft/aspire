import { randomUUID } from 'crypto';
import * as vscode from 'vscode';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';

const extensionOwnedConfigurationMarker = `__aspireAppHostLaunchServiceConfiguration_${randomUUID()}`;
const extensionOwnedConfigurationValue = randomUUID();
const externalLaunchReservationMarker = `__aspireExternalLaunchReservation_${randomUUID()}`;
const recordedDiscoveryFailureMarker = `__aspireRecordedDiscoveryFailure_${randomUUID()}`;
const recordedDiscoveryFailureValue = randomUUID();
const resolvedCliPathMarker = `__aspireResolvedCliPath_${randomUUID()}`;
const resolvedCliPathScopeMarker = `__aspireResolvedCliPathScope_${randomUUID()}`;

interface ExternalLaunchReservationMarker {
    reservationId: string;
    appHostPath: string;
    isDirectoryScope: boolean;
    kind: 'run' | 'operation';
}

export function markAspireDebugConfigurationAsExtensionOwned(configuration: vscode.DebugConfiguration): void {
    const configRecord = configuration as Record<string, unknown>;
    configRecord[extensionOwnedConfigurationMarker] = extensionOwnedConfigurationValue;
    (configuration as AspireExtendedDebugConfiguration).launchedByExtension = extensionOwnedConfigurationValue;
}

export function isAspireDebugConfigurationExtensionOwned(configuration: vscode.DebugConfiguration): boolean {
    const configRecord = configuration as Record<string, unknown>;
    return configRecord[extensionOwnedConfigurationMarker] === extensionOwnedConfigurationValue ||
        configRecord.launchedByExtension === extensionOwnedConfigurationValue;
}

export function markAspireDebugConfigurationWithExternalLaunchReservation(
    configuration: vscode.DebugConfiguration,
    reservationId: string,
    appHostPath: string,
    isDirectoryScope = false,
    kind: ExternalLaunchReservationMarker['kind'] = 'run',
): void {
    (configuration as Record<string, unknown>)[externalLaunchReservationMarker] = {
        reservationId,
        appHostPath,
        isDirectoryScope,
        kind,
    };
}

export function getAspireDebugConfigurationExternalLaunchReservation(configuration: vscode.DebugConfiguration): ExternalLaunchReservationMarker | undefined {
    const reservation = (configuration as Record<string, unknown>)[externalLaunchReservationMarker];
    if (!reservation || typeof reservation !== 'object') {
        return undefined;
    }

    const candidate = reservation as Partial<ExternalLaunchReservationMarker>;
    return typeof candidate.reservationId === 'string' &&
        typeof candidate.appHostPath === 'string' &&
        (candidate.isDirectoryScope === undefined || typeof candidate.isDirectoryScope === 'boolean') &&
        (candidate.kind === undefined || candidate.kind === 'run' || candidate.kind === 'operation')
        ? {
            reservationId: candidate.reservationId,
            appHostPath: candidate.appHostPath,
            isDirectoryScope: candidate.isDirectoryScope === true,
            kind: candidate.kind ?? 'run',
        }
        : undefined;
}

export function tryMarkAspireDebugConfigurationDiscoveryFailureRecorded(configuration: vscode.DebugConfiguration): boolean {
    const configRecord = configuration as Record<string, unknown>;
    if (configRecord[recordedDiscoveryFailureMarker] === recordedDiscoveryFailureValue) {
        return false;
    }

    configRecord[recordedDiscoveryFailureMarker] = recordedDiscoveryFailureValue;
    return true;
}

export function markAspireDebugConfigurationWithResolvedCliPath(configuration: vscode.DebugConfiguration, cliPath: string): void {
    (configuration as Record<string, unknown>)[resolvedCliPathMarker] = cliPath;
}

export function getAspireDebugConfigurationResolvedCliPath(configuration: vscode.DebugConfiguration): string | undefined {
    const cliPath = (configuration as Record<string, unknown>)[resolvedCliPathMarker];
    return typeof cliPath === 'string' ? cliPath : undefined;
}

/**
 * Records which configuration scope the CLI availability gate resolved against.
 *
 * The gate runs before VS Code substitutes variables, so a `program` such as
 * `${workspaceFolder:other}/AppHost.java` — or a relative one — is still opaque and the gate can only
 * use the initiating folder. Recording the scope lets the substituted resolver notice that the
 * concrete program belongs to a different folder and re-resolve, instead of launching that folder's
 * AppHost with another folder's configured CLI.
 */
export function markAspireDebugConfigurationWithResolvedCliPathScope(configuration: vscode.DebugConfiguration, scope: string): void {
    (configuration as Record<string, unknown>)[resolvedCliPathScopeMarker] = scope;
}

/** @see markAspireDebugConfigurationWithResolvedCliPathScope */
export function getAspireDebugConfigurationResolvedCliPathScope(configuration: vscode.DebugConfiguration): string | undefined {
    const scope = (configuration as Record<string, unknown>)[resolvedCliPathScopeMarker];
    return typeof scope === 'string' ? scope : undefined;
}

export function stripAspireDebugConfigurationProviderInternalProperties(configuration: vscode.DebugConfiguration): void {
    const configRecord = configuration as Record<string, unknown>;
    delete configRecord[extensionOwnedConfigurationMarker];
    delete configRecord[externalLaunchReservationMarker];
    delete configRecord[recordedDiscoveryFailureMarker];
    delete configRecord[resolvedCliPathMarker];
    delete configRecord[resolvedCliPathScopeMarker];
    delete configRecord.launchedByExtension;
}
