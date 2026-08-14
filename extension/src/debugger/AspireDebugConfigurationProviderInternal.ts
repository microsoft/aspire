import { randomUUID } from 'crypto';
import * as vscode from 'vscode';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';

const extensionOwnedConfigurationMarker = `__aspireAppHostLaunchServiceConfiguration_${randomUUID()}`;
const extensionOwnedConfigurationValue = randomUUID();
const externalLaunchReservationMarker = `__aspireExternalLaunchReservation_${randomUUID()}`;

interface ExternalLaunchReservationMarker {
    reservationId: string;
    appHostPath: string;
    isDirectoryScope: boolean;
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

export function markAspireDebugConfigurationWithExternalLaunchReservation(configuration: vscode.DebugConfiguration, reservationId: string, appHostPath: string, isDirectoryScope = false): void {
    (configuration as Record<string, unknown>)[externalLaunchReservationMarker] = { reservationId, appHostPath, isDirectoryScope };
}

export function getAspireDebugConfigurationExternalLaunchReservation(configuration: vscode.DebugConfiguration): ExternalLaunchReservationMarker | undefined {
    const reservation = (configuration as Record<string, unknown>)[externalLaunchReservationMarker];
    if (!reservation || typeof reservation !== 'object') {
        return undefined;
    }

    const candidate = reservation as Partial<ExternalLaunchReservationMarker>;
    return typeof candidate.reservationId === 'string' &&
        typeof candidate.appHostPath === 'string' &&
        (candidate.isDirectoryScope === undefined || typeof candidate.isDirectoryScope === 'boolean')
        ? {
            reservationId: candidate.reservationId,
            appHostPath: candidate.appHostPath,
            isDirectoryScope: candidate.isDirectoryScope === true,
        }
        : undefined;
}

export function stripAspireDebugConfigurationProviderInternalProperties(configuration: vscode.DebugConfiguration): void {
    const configRecord = configuration as Record<string, unknown>;
    delete configRecord[extensionOwnedConfigurationMarker];
    delete configRecord[externalLaunchReservationMarker];
    delete configRecord.launchedByExtension;
}
