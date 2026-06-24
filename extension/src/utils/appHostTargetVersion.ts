import { promises as fs } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { parse, type ParseError } from 'jsonc-parser';
import type { CandidateAppHostDisplayInfo } from './appHostDiscovery';

const unknownVersion = 'unknown';
const noAppHostsVersion = 'none';
const multipleVersions = 'multiple';
const maxVersionLength = 64;
// apphost_target_version is SystemMetaData telemetry, so keep values bounded to
// Aspire version shapes like "13.4.2", "13.5.0-preview.1", or
// "13.5.0-pr.18457.gabcdef" instead of emitting arbitrary project/config text.
const semverLikeVersionRegex = /^\d{1,3}\.\d{1,4}\.\d{1,4}(?:-[0-9A-Za-z][0-9A-Za-z-]{0,19}(?:\.[0-9A-Za-z][0-9A-Za-z-]{0,19}){0,5})?$/;

export type AppHostTargetVersionSummary = typeof noAppHostsVersion | typeof unknownVersion | typeof multipleVersions | string;

export async function summarizeAppHostTargetVersions(candidates: readonly CandidateAppHostDisplayInfo[]): Promise<AppHostTargetVersionSummary> {
    if (candidates.length === 0) {
        return noAppHostsVersion;
    }

    const versions = new Set<string>();
    const resolvedVersions = await Promise.all(candidates.map(async candidate =>
        normalizeVersion(candidate.aspireHostingVersion) ?? await getAppHostTargetVersion(candidate.path)));

    for (const version of resolvedVersions) {
        addVersion(versions, version);
    }

    return summarizeVersions(versions) ?? unknownVersion;
}

export async function getAppHostTargetVersion(appHostPath: string | undefined): Promise<string | undefined> {
    if (!appHostPath) {
        return undefined;
    }

    const resolvedPath = resolve(appHostPath);
    let isDirectory: boolean;
    try {
        isDirectory = (await fs.stat(resolvedPath)).isDirectory();
    }
    catch {
        return undefined;
    }

    if (isDirectory) {
        return await getAppHostTargetVersionFromDirectory(resolvedPath);
    }

    const fileVersion = await getAppHostTargetVersionFromFile(resolvedPath);
    if (fileVersion) {
        return fileVersion;
    }

    return isPolyglotAppHostFile(resolvedPath)
        ? await getConfiguredSdkVersion(dirname(resolvedPath))
        : undefined;
}

function addVersion(versions: Set<string>, version: string | undefined): void {
    if (!version) {
        return;
    }

    versions.add(version);
}

function summarizeVersions(versions: ReadonlySet<string>): string | undefined {
    if (versions.size === 0) {
        return undefined;
    }

    // apphost_target_versions is a common SystemMetaData property copied onto
    // many events. Preserve the exact version only when every AppHost agrees;
    // mixed workspaces collapse to a fixed bucket so a large workspace cannot
    // create a long or high-cardinality comma-delimited value.
    if (versions.has(multipleVersions) || versions.size > 1) {
        return multipleVersions;
    }

    return [...versions][0];
}

async function getAppHostTargetVersionFromDirectory(directoryPath: string): Promise<string | undefined> {
    let entries: string[];
    try {
        entries = await fs.readdir(directoryPath);
    }
    catch {
        return undefined;
    }

    const versionResults = await Promise.all(entries.map(async entry => {
        const entryPath = join(directoryPath, entry);
        return {
            isCSharpAppHostFile: isCSharpAppHostFile(entryPath),
            version: await getAppHostTargetVersionFromFile(entryPath),
        };
    }));

    const versions = new Set<string>();
    let sawCSharpAppHostFile = false;
    for (const result of versionResults) {
        sawCSharpAppHostFile ||= result.isCSharpAppHostFile;
        addVersion(versions, result.version);
    }

    const summary = summarizeVersions(versions);
    if (summary) {
        return summary;
    }

    return sawCSharpAppHostFile ? undefined : await getConfiguredSdkVersion(directoryPath);
}

async function getAppHostTargetVersionFromFile(filePath: string): Promise<string | undefined> {
    const extension = extname(filePath).toLowerCase();
    if (extension !== '.csproj' && extension !== '.cs') {
        return undefined;
    }

    let contents: string;
    try {
        contents = await fs.readFile(filePath, 'utf8');
    }
    catch {
        return undefined;
    }

    if (extension === '.cs') {
        return getAspireAppHostSdkVersionFromSingleFile(contents);
    }

    const projectVersion = getAspireAppHostSdkVersionFromProject(contents);
    if (projectVersion.version) {
        return projectVersion.version;
    }

    return projectVersion.referencesAspireAppHostSdk && !projectVersion.hasInlineVersion
        ? await getGlobalJsonMsBuildSdkVersion(dirname(filePath))
        : undefined;
}

function isPolyglotAppHostFile(filePath: string): boolean {
    return ['.ts', '.mts', '.cts', '.js', '.mjs', '.cjs'].includes(extname(filePath).toLowerCase());
}

function isCSharpAppHostFile(filePath: string): boolean {
    return ['.csproj', '.cs'].includes(extname(filePath).toLowerCase());
}

interface ProjectSdkVersionInfo {
    version?: string;
    referencesAspireAppHostSdk: boolean;
    hasInlineVersion: boolean;
}

function getAspireAppHostSdkVersionFromProject(contents: string): ProjectSdkVersionInfo {
    const uncommentedContents = stripXmlComments(contents);
    const sdkAttribute = getAspireAppHostSdkVersionFromProjectSdkAttribute(uncommentedContents);
    const sdkElement = getAspireAppHostSdkVersionFromSdkElement(uncommentedContents);
    const propertyVersion = getAspireHostingSdkVersionProperty(uncommentedContents);

    return {
        version: sdkAttribute.version ?? sdkElement.version ?? propertyVersion,
        referencesAspireAppHostSdk: sdkAttribute.referencesAspireAppHostSdk || sdkElement.referencesAspireAppHostSdk,
        hasInlineVersion: sdkAttribute.hasInlineVersion || sdkElement.hasInlineVersion,
    };
}

function stripXmlComments(contents: string): string {
    // Project files can contain inactive SDK declarations in XML comments, for example:
    //   <!-- <Project Sdk="Aspire.AppHost.Sdk/1.2.3"> -->
    // Remove comments before applying the lightweight SDK regexes so telemetry cannot report
    // a version from an inactive project fragment.
    return contents.replace(/<!--[\s\S]*?-->/g, '');
}

function getAspireAppHostSdkVersionFromProjectSdkAttribute(contents: string): ProjectSdkVersionInfo {
    // SDK-style AppHosts usually target Aspire through the Project SDK:
    //   <Project Sdk="Aspire.AppHost.Sdk/13.5.0">
    // Multiple SDKs are semicolon-delimited:
    //   <Project Sdk="Microsoft.NET.Sdk; Aspire.AppHost.Sdk/13.5.0">
    // Standard MSBuild SDK resolution also allows the version to be omitted here
    // and supplied by global.json's "msbuild-sdks" object.
    const projectSdkMatch = /<Project\b[^>]*\bSdk\s*=\s*(["'])(?<sdks>.*?)\1/is.exec(contents);
    const sdkAttribute = projectSdkMatch?.groups?.sdks;
    if (!sdkAttribute) {
        return noProjectSdkVersionInfo();
    }

    let referencesAspireAppHostSdk = false;
    let hasInlineVersion = false;
    for (const sdk of sdkAttribute.split(';')) {
        const trimmedSdk = sdk.trim();
        const sdkMatch = /^Aspire\.AppHost\.Sdk(?:\/(?<version>[^;\s"']+))?$/i.exec(trimmedSdk);
        if (!sdkMatch) {
            continue;
        }

        referencesAspireAppHostSdk = true;
        if (sdkMatch.groups?.version !== undefined) {
            hasInlineVersion = true;
        }

        const version = normalizeVersion(sdkMatch.groups?.version);
        if (version) {
            return { version, referencesAspireAppHostSdk, hasInlineVersion };
        }
    }

    return { referencesAspireAppHostSdk, hasInlineVersion };
}

function getAspireAppHostSdkVersionFromSdkElement(contents: string): ProjectSdkVersionInfo {
    // Older projects can express the SDK as an element:
    //   <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.0" />
    // The Version attribute may be omitted when global.json supplies the SDK version.
    const sdkElementRegex = /<Sdk\b(?=[^>]*\bName\s*=\s*(["'])Aspire\.AppHost\.Sdk\1)[^>]*>/gis;
    let referencesAspireAppHostSdk = false;
    let hasInlineVersion = false;
    for (const match of contents.matchAll(sdkElementRegex)) {
        referencesAspireAppHostSdk = true;
        const versionMatch = /\bVersion\s*=\s*(["'])(?<version>.*?)\1/is.exec(match[0]);
        if (versionMatch) {
            hasInlineVersion = true;
        }

        const version = normalizeVersion(versionMatch?.groups?.version);
        if (version) {
            return { version, referencesAspireAppHostSdk, hasInlineVersion };
        }
    }

    return { referencesAspireAppHostSdk, hasInlineVersion };
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

async function getConfiguredSdkVersion(startDirectory: string): Promise<string | undefined> {
    for (let directory = resolve(startDirectory); ; directory = dirname(directory)) {
        const version = await getConfiguredSdkVersionInDirectory(directory);
        if (version) {
            return version;
        }

        const parent = dirname(directory);
        if (parent === directory) {
            return undefined;
        }
    }
}

async function getConfiguredSdkVersionInDirectory(directory: string): Promise<string | undefined> {
    return await readSdkVersionFromConfigFile(join(directory, 'aspire.config.json'))
        ?? await readSdkVersionFromConfigFile(join(directory, '.aspire', 'settings.json'));
}

async function readSdkVersionFromConfigFile(configPath: string): Promise<string | undefined> {
    const parsed = await readJsoncFile(configPath);
    const sdk = getJsonObjectProperty(parsed, 'sdk');
    return normalizeVersion(sdk?.version)
        ?? normalizeVersion(parsed?.sdkVersion);
}

async function getGlobalJsonMsBuildSdkVersion(startDirectory: string): Promise<string | undefined> {
    for (let directory = resolve(startDirectory); ; directory = dirname(directory)) {
        const version = await readAspireAppHostSdkVersionFromGlobalJson(join(directory, 'global.json'));
        if (version) {
            return version;
        }

        const parent = dirname(directory);
        if (parent === directory) {
            return undefined;
        }
    }
}

async function readAspireAppHostSdkVersionFromGlobalJson(globalJsonPath: string): Promise<string | undefined> {
    const parsed = await readJsoncFile(globalJsonPath);
    const msBuildSdks = getJsonObjectProperty(parsed, 'msbuild-sdks');
    // MSBuild's SDK resolver reads this global.json shape:
    //   { "msbuild-sdks": { "Aspire.AppHost.Sdk": "13.5.0" } }
    // See https://learn.microsoft.com/visualstudio/msbuild/how-to-use-project-sdk#how-project-sdks-are-resolved.
    return normalizeVersion(msBuildSdks?.['Aspire.AppHost.Sdk']);
}

async function readJsoncFile(filePath: string): Promise<Record<string, unknown> | undefined> {
    let contents: string;
    try {
        contents = await fs.readFile(filePath, 'utf8');
    }
    catch {
        return undefined;
    }

    const errors: ParseError[] = [];
    const parsed = parse(contents, errors, { allowTrailingComma: true }) as unknown;
    return errors.length === 0 && isJsonObject(parsed) ? parsed : undefined;
}

function getJsonObjectProperty(obj: Record<string, unknown> | undefined, propertyName: string): Record<string, unknown> | undefined {
    const value = obj?.[propertyName];
    return isJsonObject(value) ? value : undefined;
}

function isJsonObject(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function normalizeVersion(version: unknown): string | undefined {
    if (typeof version !== 'string') {
        return undefined;
    }

    const trimmedVersion = version.trim();
    if (trimmedVersion.length === 0 || trimmedVersion.length > maxVersionLength) {
        return undefined;
    }

    return semverLikeVersionRegex.test(trimmedVersion)
        ? trimmedVersion
        : undefined;
}

function noProjectSdkVersionInfo(): ProjectSdkVersionInfo {
    return {
        referencesAspireAppHostSdk: false,
        hasInlineVersion: false,
    };
}
