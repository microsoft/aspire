import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { stripComments } from 'jsonc-parser';
import type { CandidateAppHostDisplayInfo } from './appHostDiscovery';

const unknownVersion = 'unknown';
const noAppHostsVersion = 'none';

export function summarizeAppHostTargetVersions(candidates: readonly CandidateAppHostDisplayInfo[]): string {
    if (candidates.length === 0) {
        return noAppHostsVersion;
    }

    const versions = new Set<string>();
    for (const candidate of candidates) {
        const version = normalizeVersion(candidate.aspireHostingVersion) ?? getAppHostTargetVersion(candidate.path);
        if (version) {
            versions.add(version);
        }
    }

    return versions.size === 0
        ? unknownVersion
        : [...versions].sort((left, right) => left.localeCompare(right)).join(',');
}

export function getAppHostTargetVersion(appHostPath: string | undefined): string | undefined {
    if (!appHostPath) {
        return undefined;
    }

    const resolvedPath = resolve(appHostPath);
    if (!existsSync(resolvedPath)) {
        return undefined;
    }

    let isDirectory: boolean;
    try {
        isDirectory = statSync(resolvedPath).isDirectory();
    }
    catch {
        return undefined;
    }

    if (isDirectory) {
        return getAppHostTargetVersionFromDirectory(resolvedPath);
    }

    const fileVersion = getAppHostTargetVersionFromFile(resolvedPath);
    if (fileVersion) {
        return fileVersion;
    }

    return isPolyglotAppHostFile(resolvedPath)
        ? getConfiguredSdkVersion(dirname(resolvedPath))
        : undefined;
}

function getAppHostTargetVersionFromDirectory(directoryPath: string): string | undefined {
    let entries: string[];
    try {
        entries = readdirSync(directoryPath);
    }
    catch {
        return undefined;
    }

    const versions = new Set<string>();
    let sawCSharpAppHostFile = false;
    for (const entry of entries) {
        const entryPath = join(directoryPath, entry);
        sawCSharpAppHostFile ||= isCSharpAppHostFile(entryPath);
        const version = getAppHostTargetVersionFromFile(entryPath);
        if (version) {
            versions.add(version);
        }
    }

    if (versions.size > 0) {
        return [...versions].sort((left, right) => left.localeCompare(right)).join(',');
    }

    return sawCSharpAppHostFile ? undefined : getConfiguredSdkVersion(directoryPath);
}

function getAppHostTargetVersionFromFile(filePath: string): string | undefined {
    const extension = extname(filePath).toLowerCase();
    if (extension !== '.csproj' && extension !== '.cs') {
        return undefined;
    }

    let contents: string;
    try {
        contents = readFileSync(filePath, 'utf8');
    }
    catch {
        return undefined;
    }

    return extension === '.csproj'
        ? getAspireAppHostSdkVersionFromProject(contents)
        : getAspireAppHostSdkVersionFromSingleFile(contents);
}

function isPolyglotAppHostFile(filePath: string): boolean {
    return ['.ts', '.mts', '.cts', '.js', '.mjs', '.cjs'].includes(extname(filePath).toLowerCase());
}

function isCSharpAppHostFile(filePath: string): boolean {
    return ['.csproj', '.cs'].includes(extname(filePath).toLowerCase());
}

function getAspireAppHostSdkVersionFromProject(contents: string): string | undefined {
    return getAspireAppHostSdkVersionFromProjectSdkAttribute(contents)
        ?? getAspireAppHostSdkVersionFromSdkElement(contents)
        ?? getAspireHostingSdkVersionProperty(contents);
}

function getAspireAppHostSdkVersionFromProjectSdkAttribute(contents: string): string | undefined {
    // SDK-style AppHosts usually target Aspire through the Project SDK:
    //   <Project Sdk="Aspire.AppHost.Sdk/13.5.0">
    // Multiple SDKs are semicolon-delimited:
    //   <Project Sdk="Microsoft.NET.Sdk; Aspire.AppHost.Sdk/13.5.0">
    const projectSdkMatch = /<Project\b[^>]*\bSdk\s*=\s*(["'])(?<sdks>.*?)\1/is.exec(contents);
    const sdkAttribute = projectSdkMatch?.groups?.sdks;
    if (!sdkAttribute) {
        return undefined;
    }

    for (const sdk of sdkAttribute.split(';')) {
        const trimmedSdk = sdk.trim();
        const versionMatch = /^Aspire\.AppHost\.Sdk\/(?<version>[^;\s"']+)$/i.exec(trimmedSdk);
        const version = normalizeVersion(versionMatch?.groups?.version);
        if (version) {
            return version;
        }
    }

    return undefined;
}

function getAspireAppHostSdkVersionFromSdkElement(contents: string): string | undefined {
    // Older projects can express the SDK as an element:
    //   <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.0" />
    const sdkElementRegex = /<Sdk\b(?=[^>]*\bName\s*=\s*(["'])Aspire\.AppHost\.Sdk\1)(?=[^>]*\bVersion\s*=\s*(["'])(?<version>.*?)\2)[^>]*>/gis;
    for (const match of contents.matchAll(sdkElementRegex)) {
        const version = normalizeVersion(match.groups?.version);
        if (version) {
            return version;
        }
    }

    return undefined;
}

function getAspireHostingSdkVersionProperty(contents: string): string | undefined {
    // Polyglot generated server projects carry the evaluated SDK version as:
    //   <AspireHostingSDKVersion>13.5.0</AspireHostingSDKVersion>
    // This can also appear in SDK-imported props for C# projects. Prefer the
    // explicit AppHost SDK forms above when they are present.
    const propertyMatch = /<AspireHostingSDKVersion>\s*(?<version>[^<\s]+)\s*<\/AspireHostingSDKVersion>/i.exec(contents);
    return normalizeVersion(propertyMatch?.groups?.version);
}

function getAspireAppHostSdkVersionFromSingleFile(contents: string): string | undefined {
    // Single-file C# AppHosts target Aspire with a file directive:
    //   #:sdk Aspire.AppHost.Sdk@13.5.0
    const directiveMatch = /^[ \t]*#:sdk[ \t]+Aspire\.AppHost\.Sdk@(?<version>\S+)/im.exec(contents);
    return normalizeVersion(directiveMatch?.groups?.version);
}

function getConfiguredSdkVersion(startDirectory: string): string | undefined {
    for (let directory = resolve(startDirectory); ; directory = dirname(directory)) {
        const version = getConfiguredSdkVersionInDirectory(directory);
        if (version) {
            return version;
        }

        const parent = dirname(directory);
        if (parent === directory) {
            return undefined;
        }
    }
}

function getConfiguredSdkVersionInDirectory(directory: string): string | undefined {
    return readSdkVersionFromConfigFile(join(directory, 'aspire.config.json'))
        ?? readSdkVersionFromConfigFile(join(directory, '.aspire', 'settings.json'));
}

function readSdkVersionFromConfigFile(configPath: string): string | undefined {
    if (!existsSync(configPath)) {
        return undefined;
    }

    try {
        const parsed = JSON.parse(stripComments(readFileSync(configPath, 'utf8')));
        return normalizeVersion(parsed?.sdk?.version)
            ?? normalizeVersion(parsed?.sdkVersion);
    }
    catch {
        return undefined;
    }
}

function normalizeVersion(version: unknown): string | undefined {
    return typeof version === 'string' && version.trim() !== ''
        ? version.trim()
        : undefined;
}
