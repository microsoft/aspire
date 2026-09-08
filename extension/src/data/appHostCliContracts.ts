import { aspireCliCommandFailed, aspireCliOutputParseFailed } from '../loc/strings';

export interface ResourceUrlJson {
    name: string | null;
    displayName: string | null;
    url: string;
    isInternal: boolean;
}

export interface ResourceCommandJson {
    displayName?: string | null;
    description: string | null;
    visibility?: string | null;
    state?: string | null;
    sortOrder?: number | null;
    argumentInputs?: ResourceCommandArgumentInputJson[] | null;
}

// Resource command argument input types. Values match the strings emitted by the CLI
// JSON contract (ResourceCommandArgumentJson.InputType in
// src/Shared/Model/Serialization/ResourceJson.cs).
export const ResourceCommandInputType = {
    Text: 'Text',
    SecretText: 'SecretText',
    Choice: 'Choice',
    Boolean: 'Boolean',
    Number: 'Number',
} as const;

export type ResourceCommandInputType = typeof ResourceCommandInputType[keyof typeof ResourceCommandInputType];

export interface ResourceCommandArgumentDynamicLoadingJson {
    alwaysLoadOnStart?: boolean;
    dependsOnInputs?: string[] | null;
}

// Mirrors the CLI JSON contract in src/Shared/Model/Serialization/ResourceJson.cs
// (`ResourceCommandArgumentJson`), populated by Aspire.Cli's ResourceSnapshotMapper.
export interface ResourceCommandArgumentInputJson {
    name: string;
    label: string | null;
    description: string | null;
    enableDescriptionMarkdown?: boolean;
    inputType: ResourceCommandInputType;
    required?: boolean;
    placeholder: string | null;
    value: string | null;
    options: Record<string, string | null> | null;
    allowCustomChoice?: boolean;
    disabled?: boolean;
    maxLength: number | null;
    dynamicLoading?: ResourceCommandArgumentDynamicLoadingJson | null;
}

export interface ResourceHealthReportJson {
    status: string | null;
    description: string | null;
    exceptionMessage: string | null;
}

export interface ResourceJson {
    name: string;
    displayName: string | null;
    resourceType: string;
    source?: string | null;
    state: string | null;
    stateStyle: string | null;
    healthStatus: string | null;
    healthReports: Record<string, ResourceHealthReportJson> | null;
    exitCode: number | null;
    dashboardUrl: string | null;
    urls: ResourceUrlJson[] | null;
    commands: Record<string, ResourceCommandJson> | null;
    properties: Record<string, string | null> | null;
}

export function isResourceNameMatch(
    resource: ResourceJson,
    resourceName: string,
    includeDisplayName = true): boolean {
    return equalsIgnoreCase(resource.name, resourceName) ||
        (includeDisplayName && typeof resource.displayName === 'string' && equalsIgnoreCase(resource.displayName, resourceName));
}

export function resolveResourceNameMatches(
    resources: readonly ResourceJson[],
    resourceName: string): readonly ResourceJson[] {
    const exactMatches = resources.filter(resource => isResourceNameMatch(resource, resourceName, false));
    return exactMatches.length > 0
        ? exactMatches
        : resources.filter(resource => isResourceNameMatch(resource, resourceName));
}

function equalsIgnoreCase(left: string, right: string): boolean {
    return left.toUpperCase() === right.toUpperCase();
}

export interface AppHostDisplayInfo {
    appHostPath: string;
    appHostPid: number;
    status?: string;
    cliPid: number | null;
    dashboardUrl: string | null;
    logFilePath?: string | null;
    resources: ResourceJson[] | null | undefined;
}

export interface DescribeSnapshotJson {
    resources?: ResourceJson[];
}

export class AspireCliNotInstalledError extends Error {
    constructor(message: string) {
        super(message);
        this.name = 'AspireCliNotInstalledError';
    }
}

export class AspireCliFailedError extends Error {
    constructor(
        public readonly command: string,
        public readonly exitCode: number | null,
        public readonly stdout: string,
        public readonly stderr: string) {
        super(aspireCliCommandFailed(command, String(exitCode), ''));
        this.name = 'AspireCliFailedError';
    }
}

export class AspireCliParseError extends Error {
    constructor(
        public readonly command: string,
        public readonly output: string,
        innerError: unknown) {
        super(aspireCliOutputParseFailed(command, String(innerError)));
        this.name = 'AspireCliParseError';
    }
}

/**
 * Captured output from a hidden `aspire resource ...` execution. `stdout` carries the rendered
 * command value (when the command returns one); `stderr` carries human-readable status/errors.
 */
export interface ResourceCommandExecutionOutput {
    stdout: string;
    stderr: string;
}

export type ViewMode = 'workspace' | 'global';
