import * as fs from 'fs';
import { AspireCommandType } from '../dcp/types';
import { classifyAppHostDirectory, classifyAppHostPath } from '../utils/appHostLanguage';
import { bucketAspireCommand } from '../utils/telemetryBuckets';

export async function getLaunchTelemetryProperties(appHostPath: string, command: AspireCommandType, noDebug: boolean, executionSuppressed: boolean) {
    const isDirectory = isDirectoryForTelemetry(appHostPath);
    return {
        mode: noDebug ? 'run' : 'debug',
        command: bucketAspireCommand(command),
        apphost_language: isDirectory ? await classifyAppHostDirectory(appHostPath) : classifyAppHostPath(appHostPath),
        execution_suppressed: executionSuppressed ? 'true' : 'false',
    };
}

function isDirectoryForTelemetry(appHostPath: string): boolean {
    try {
        return fs.statSync(appHostPath, { throwIfNoEntry: false })?.isDirectory() === true;
    }
    catch {
        return false;
    }
}

export function isE2eDebugLaunchSuppressed(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE === 'true' &&
        !!process.env.ASPIRE_EXTENSION_E2E_STATE_FILE &&
        !!process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE &&
        process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH === 'true';
}
