import { randomUUID } from 'crypto';
import * as vscode from 'vscode';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';
import { appHostCliPathConfigKey } from './AspireDebugConfigurationMetadata';

const extensionOwnedConfigurationMarker = `__aspireAppHostLaunchServiceConfiguration_${randomUUID()}`;
const extensionOwnedConfigurationValue = randomUUID();
const externalLaunchReservationMarker = `__aspireExternalLaunchReservation_${randomUUID()}`;
const trustedCliPathMarker = `__aspireTrustedCliPath_${randomUUID()}`;
const trustedCliPathValue = randomUUID();

interface ExternalLaunchReservationMarker {
    reservationId: string;
    appHostPath: string;
    isDirectoryScope: boolean;
}

interface TrustedCliPathMarker {
    value: string;
    cliPath: string;
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

export function markAspireDebugConfigurationCliPathAsTrusted(configuration: vscode.DebugConfiguration): void {
    const cliPath = configuration[appHostCliPathConfigKey];
    if (typeof cliPath !== 'string') {
        return;
    }

    (configuration as Record<string, unknown>)[trustedCliPathMarker] = {
        value: trustedCliPathValue,
        cliPath,
    };
}

export function getAspireDebugConfigurationTrustedCliPath(configuration: vscode.DebugConfiguration): string | undefined {
    const marker = (configuration as Record<string, unknown>)[trustedCliPathMarker];
    if (!marker || typeof marker !== 'object') {
        return undefined;
    }

    const candidate = marker as Partial<TrustedCliPathMarker>;
    return candidate.value === trustedCliPathValue &&
        typeof candidate.cliPath === 'string' &&
        configuration[appHostCliPathConfigKey] === candidate.cliPath
        ? candidate.cliPath
        : undefined;
}

export function stripAspireDebugConfigurationProviderInternalProperties(configuration: vscode.DebugConfiguration): void {
    const configRecord = configuration as Record<string, unknown>;
    delete configRecord[extensionOwnedConfigurationMarker];
    delete configRecord[externalLaunchReservationMarker];
    delete configRecord[trustedCliPathMarker];
    delete configRecord.launchedByExtension;
}
