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
        addCandidateVersion(versions, version);
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

function addCandidateVersion(versions: Set<string>, version: string | undefined): void {
    addVersion(versions, version ?? unknownVersion);
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
        return await getAppHostTargetVersionInfoFromDirectoryEntry(directoryPath, entry, entries);
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
    return (await getAppHostTargetVersionInfoFromFile(filePath)).version;
}

async function getAppHostTargetVersionInfoFromDirectoryEntry(directoryPath: string, entry: string, entries: readonly string[]): Promise<FileTargetVersionInfo> {
    const entryPath = join(directoryPath, entry);
    const versionInfo = await getAppHostTargetVersionInfoFromFile(entryPath);

    if (extname(entry).toLowerCase() !== '.cs') {
        return versionInfo;
    }

    // Match the CLI's single-file AppHost detection so unrelated helper.cs files
    // cannot block a polyglot directory from using aspire.config.json.
    return isSingleFileCSharpAppHostEntry(entry, entries)
        ? versionInfo
        : noFileTargetVersionInfo();
}

interface FileTargetVersionInfo {
    version?: string;
    isCSharpAppHostFile: boolean;
}

async function getAppHostTargetVersionInfoFromFile(filePath: string): Promise<FileTargetVersionInfo> {
    const extension = extname(filePath).toLowerCase();
    if (extension !== '.csproj' && extension !== '.cs') {
        return noFileTargetVersionInfo();
    }

    let contents: string;
    try {
        contents = stripLeadingByteOrderMark(await fs.readFile(filePath, 'utf8'));
    }
    catch {
        return noFileTargetVersionInfo();
    }

    const uncommentedContents = stripXmlComments(contents);

    if (extension === '.cs') {
        const singleFileVersion = getAspireAppHostSdkVersionFromSingleFile(contents);
        return {
            version: singleFileVersion.version,
            isCSharpAppHostFile: singleFileVersion.referencesAspireAppHostSdk,
        };
    }

    const packageVersion = getAspireHostingPackageVersionFromProject(uncommentedContents);
    if (packageVersion.version) {
        return {
            version: packageVersion.version,
            isCSharpAppHostFile: true,
        };
    }

    const centralPackageVersion = packageVersion.referencesAspireHostingPackage && !packageVersion.hasInlineVersion
        ? await getCentralAspireHostingPackageVersion(dirname(filePath))
        : undefined;

    if (centralPackageVersion) {
        return {
            version: centralPackageVersion,
            isCSharpAppHostFile: true,
        };
    }

    const projectVersion = getAspireAppHostSdkVersionFromProject(uncommentedContents);
    if (projectVersion.version) {
        return {
            version: projectVersion.version,
            isCSharpAppHostFile: true,
        };
    }

    const globalJsonVersion = projectVersion.referencesAspireAppHostSdk && !projectVersion.hasInlineVersion
        ? await getGlobalJsonMsBuildSdkVersion(dirname(filePath))
        : undefined;

    return {
        version: globalJsonVersion,
        isCSharpAppHostFile: packageVersion.referencesAspireHostingPackage || projectVersion.referencesAspireAppHostSdk,
    };
}

function isPolyglotAppHostFile(filePath: string): boolean {
    return ['.ts', '.mts', '.cts', '.js', '.mjs', '.cjs'].includes(extname(filePath).toLowerCase());
}

function isSingleFileCSharpAppHostEntry(entry: string, entries: readonly string[]): boolean {
    return entry.toLowerCase() === 'apphost.cs'
        && !entries.some(entry => extname(entry).toLowerCase() === '.csproj');
}

interface ProjectSdkVersionInfo {
    version?: string;
    referencesAspireAppHostSdk: boolean;
    hasInlineVersion: boolean;
}

interface PackageVersionInfo {
    version?: string;
    referencesAspireHostingPackage: boolean;
    hasInlineVersion: boolean;
}

interface SingleFileSdkVersionInfo {
    version?: string;
    referencesAspireAppHostSdk: boolean;
}

function getAspireAppHostSdkVersionFromProject(contents: string): ProjectSdkVersionInfo {
    const sdkAttribute = getAspireAppHostSdkVersionFromProjectSdkAttribute(contents);
    const sdkElement = getAspireAppHostSdkVersionFromSdkElement(contents);
    const propertyVersion = getAspireHostingSdkVersionProperty(contents);

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

function getAspireHostingPackageVersionFromProject(contents: string): PackageVersionInfo {
    return getAspireHostingPackageVersionFromItems(contents, ['PackageReference', 'AspireProjectOrPackageReference', 'PackageVersion']);
}

function getAspireHostingPackageVersionFromPackageVersionsFile(contents: string): PackageVersionInfo {
    return getAspireHostingPackageVersionFromItems(contents, ['PackageVersion']);
}

function getAspireHostingPackageVersionFromItems(contents: string, itemNames: readonly string[]): PackageVersionInfo {
    // Older C# AppHosts can target Aspire through package metadata instead of the AppHost SDK:
    //   <PackageReference Include="Aspire.Hosting.AppHost" Version="8.2.1" />
    //   <PackageReference Include="Aspire.Hosting"><Version>8.2.1</Version></PackageReference>
    //   <PackageVersion Include="Aspire.Hosting" Version="8.2.2" />
    // The CLI prefers Aspire.Hosting before Aspire.Hosting.AppHost; keep the fallback aligned so
    // old CLIs that omit aspireHostingVersion report the same version source as new CLIs.
    const packageIds = ['Aspire.Hosting', 'Aspire.Hosting.AppHost'];
    let referencesAspireHostingPackage = false;
    let hasInlineVersion = false;

    for (const itemName of itemNames) {
        const items = getMsBuildItems(contents, itemName);
        for (const packageId of packageIds) {
            for (const item of items) {
                const identity = getMsBuildItemIdentity(item);
                if (identity !== packageId) {
                    continue;
                }

                referencesAspireHostingPackage = true;
                const versionMetadata = getMsBuildVersionMetadata(item);
                hasInlineVersion ||= versionMetadata.hasVersion;
                if (versionMetadata.version) {
                    return { version: versionMetadata.version, referencesAspireHostingPackage, hasInlineVersion };
                }
            }
        }
    }

    return { referencesAspireHostingPackage, hasInlineVersion };
}

function getMsBuildItems(contents: string, itemName: string): string[] {
    const itemRegex = new RegExp(`<${itemName}\\b[^>]*(?:/>|>[\\s\\S]*?</${itemName}>)`, 'gi');
    return [...contents.matchAll(itemRegex)].map(match => match[0]);
}

function getMsBuildItemIdentity(item: string): string | undefined {
    return getXmlAttribute(item, 'Include')
        ?? getXmlAttribute(item, 'Update');
}

function getMsBuildVersionMetadata(item: string): { version?: string; hasVersion: boolean } {
    const version = getXmlAttribute(item, 'VersionOverride')
        ?? getXmlAttribute(item, 'Version')
        ?? getXmlElementText(item, 'Version');

    return {
        version: normalizeVersion(version),
        hasVersion: version !== undefined,
    };
}

function getXmlAttribute(xml: string, attributeName: string): string | undefined {
    const openingTag = /^<[^>]+>/s.exec(xml)?.[0];
    const attributeMatch = new RegExp(`\\b${attributeName}\\s*=\\s*(["'])(?<value>.*?)\\1`, 'is').exec(openingTag ?? '');
    return attributeMatch?.groups?.value;
}

function getXmlElementText(xml: string, elementName: string): string | undefined {
    const elementMatch = new RegExp(`<${elementName}>\\s*(?<value>[^<\\s]+)\\s*</${elementName}>`, 'i').exec(xml);
    return elementMatch?.groups?.value;
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

function getAspireAppHostSdkVersionFromSingleFile(contents: string): SingleFileSdkVersionInfo {
    // Single-file C# AppHosts target Aspire with a file directive:
    //   #:sdk Aspire.AppHost.Sdk@13.5.0
    const directiveMatch = /^[ \t]*#:sdk[ \t]+Aspire\.AppHost\.Sdk(?:@(?<version>\S+))?/im.exec(contents);
    return {
        version: normalizeVersion(directiveMatch?.groups?.version),
        referencesAspireAppHostSdk: directiveMatch !== null,
    };
}

async function getConfiguredSdkVersion(startDirectory: string): Promise<string | undefined> {
    for (let directory = resolve(startDirectory); ; directory = dirname(directory)) {
        const result = await getConfiguredSdkVersionInDirectory(directory);
        if (result.version || result.foundConfig) {
            return result.version;
        }

        const parent = dirname(directory);
        if (parent === directory) {
            return undefined;
        }
    }
}

interface ConfiguredSdkVersionInfo {
    version?: string;
    foundConfig: boolean;
}

async function getConfiguredSdkVersionInDirectory(directory: string): Promise<ConfiguredSdkVersionInfo> {
    const configVersion = await readSdkVersionFromConfigFile(join(directory, 'aspire.config.json'));
    if (configVersion.version || configVersion.foundConfig) {
        return configVersion;
    }

    return await readSdkVersionFromConfigFile(join(directory, '.aspire', 'settings.json'));
}

async function readSdkVersionFromConfigFile(configPath: string): Promise<ConfiguredSdkVersionInfo> {
    const file = await readJsoncFile(configPath);
    if (!file.exists) {
        return { foundConfig: false };
    }

    const parsed = file.value;
    const sdk = getJsonObjectProperty(parsed, 'sdk');
    return {
        version: normalizeVersion(sdk?.version)
            ?? normalizeVersion(parsed?.sdkVersion),
        foundConfig: true,
    };
}

async function getGlobalJsonMsBuildSdkVersion(startDirectory: string): Promise<string | undefined> {
    for (let directory = resolve(startDirectory); ; directory = dirname(directory)) {
        const result = await readAspireAppHostSdkVersionFromGlobalJson(join(directory, 'global.json'));
        if (result.version || result.foundConfig) {
            return result.version;
        }

        const parent = dirname(directory);
        if (parent === directory) {
            return undefined;
        }
    }
}

async function getCentralAspireHostingPackageVersion(startDirectory: string): Promise<string | undefined> {
    for (let directory = resolve(startDirectory); ; directory = dirname(directory)) {
        const result = await readAspireHostingPackageVersionFromDirectoryPackages(join(directory, 'Directory.Packages.props'));
        if (result.version || result.foundConfig) {
            return result.version;
        }

        const parent = dirname(directory);
        if (parent === directory) {
            return undefined;
        }
    }
}

async function readAspireAppHostSdkVersionFromGlobalJson(globalJsonPath: string): Promise<ConfiguredSdkVersionInfo> {
    const file = await readJsoncFile(globalJsonPath);
    if (!file.exists) {
        return { foundConfig: false };
    }

    const parsed = file.value;
    const msBuildSdks = getJsonObjectProperty(parsed, 'msbuild-sdks');
    // MSBuild's SDK resolver reads this global.json shape:
    //   { "msbuild-sdks": { "Aspire.AppHost.Sdk": "13.5.0" } }
    // See https://learn.microsoft.com/visualstudio/msbuild/how-to-use-project-sdk#how-project-sdks-are-resolved.
    return {
        version: normalizeVersion(msBuildSdks?.['Aspire.AppHost.Sdk']),
        foundConfig: true,
    };
}

async function readAspireHostingPackageVersionFromDirectoryPackages(packagesPath: string): Promise<ConfiguredSdkVersionInfo> {
    let contents: string;
    try {
        contents = stripXmlComments(stripLeadingByteOrderMark(await fs.readFile(packagesPath, 'utf8')));
    }
    catch {
        return { foundConfig: false };
    }

    const packageVersion = getAspireHostingPackageVersionFromPackageVersionsFile(contents);
    return {
        version: packageVersion.version,
        foundConfig: true,
    };
}

interface JsoncFileResult {
    exists: boolean;
    value?: Record<string, unknown>;
}

async function readJsoncFile(filePath: string): Promise<JsoncFileResult> {
    let contents: string;
    try {
        contents = stripLeadingByteOrderMark(await fs.readFile(filePath, 'utf8'));
    }
    catch {
        return { exists: false };
    }

    const errors: ParseError[] = [];
    const parsed = parse(contents, errors, { allowTrailingComma: true }) as unknown;
    return {
        exists: true,
        value: errors.length === 0 && isJsonObject(parsed) ? parsed : undefined,
    };
}

function stripLeadingByteOrderMark(contents: string): string {
    // Node's UTF-8 text decoder exposes a file BOM as U+FEFF at offset 0. Strip
    // only that exact prefix so anchored parsers, like "#:sdk" single-file AppHost
    // detection and jsonc-parser, see the same first character users see.
    return contents.startsWith('\uFEFF') ? contents.slice(1) : contents;
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

function noFileTargetVersionInfo(): FileTargetVersionInfo {
    return {
        isCSharpAppHostFile: false,
    };
}
