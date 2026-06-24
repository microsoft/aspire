import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { CandidateAppHostDisplayInfo } from './appHostDiscovery';

/**
 * Coarse AppHost language classification used for telemetry. We deliberately
 * collapse the per-AppHost language strings emitted by `aspire ls` (which can
 * include forms like `typescript/nodejs`) into a small, stable set so the
 * telemetry dimension is meaningful for cohorts without enumerating every
 * runtime variant.
 *
 *  - `csharp`     : every detected AppHost reports a C# variant.
 *  - `typescript` : every detected AppHost reports a TypeScript / Node variant.
 *  - `polyglot`   : at least one AppHost of each language family is present,
 *                   or an unknown language is mixed with a known one. This is
 *                   the headline signal Damian asked us to capture.
 *  - `unknown`    : we found AppHosts but couldn't classify any of them.
 *  - `none`       : no AppHosts were detected at all.
 */
export type AppHostLanguageSummary = 'csharp' | 'typescript' | 'polyglot' | 'unknown' | 'none';

/**
 * Normalizes a language string from `aspire ls --format json` to a coarse
 * family. Keep this list narrow — adding noisy buckets defeats the purpose of
 * the summary. Anything we don't recognize is grouped as `'other'` so that a
 * mixed workspace still reports `polyglot` rather than hiding the diversity.
 */
function languageFamily(raw: string | null | undefined): 'csharp' | 'typescript' | 'other' | undefined {
    if (!raw) {
        return undefined;
    }
    const value = raw.toLowerCase();
    if (value === 'csharp' || value === 'c#') {
        return 'csharp';
    }
    if (value === 'typescript' || value.startsWith('typescript/') || value === 'javascript' || value.startsWith('javascript/')) {
        return 'typescript';
    }
    return 'other';
}

export function summarizeAppHostLanguages(candidates: readonly CandidateAppHostDisplayInfo[]): AppHostLanguageSummary {
    if (candidates.length === 0) {
        return 'none';
    }

    let sawCsharp = false;
    let sawTypescript = false;
    let sawOther = false;
    let sawAny = false;

    for (const candidate of candidates) {
        const family = languageFamily(candidate.language);
        if (family === undefined) {
            continue;
        }
        sawAny = true;
        if (family === 'csharp') {
            sawCsharp = true;
        }
        else if (family === 'typescript') {
            sawTypescript = true;
        }
        else {
            sawOther = true;
        }
    }

    if (!sawAny) {
        return 'unknown';
    }

    const distinctFamilies = Number(sawCsharp) + Number(sawTypescript) + Number(sawOther);
    if (distinctFamilies > 1) {
        return 'polyglot';
    }
    if (sawCsharp) {
        return 'csharp';
    }
    if (sawTypescript) {
        return 'typescript';
    }
    return 'unknown';
}

/**
 * Coarse single-AppHost classification used by the debug-session telemetry path
 * where we have a concrete program/project path but no `aspire ls` candidate.
 * Mirrors {@link summarizeAppHostLanguages} categories so dashboard cohorts can
 * combine the two signals.
 *
 * When `appHostPath` points at a directory (rather than a file), callers should
 * use {@link classifyAppHostDirectory} which peeks for marker files. This entry
 * point only looks at the file extension.
 */
export function classifyAppHostPath(appHostPath: string | undefined): 'csharp' | 'typescript' | 'unknown' {
    if (!appHostPath) {
        return 'unknown';
    }
    const lower = appHostPath.toLowerCase();
    if (lower.endsWith('.csproj') || lower.endsWith('.fsproj') || lower.endsWith('.vbproj') || lower.endsWith('.cs')) {
        return 'csharp';
    }
    if (lower.endsWith('.ts') || lower.endsWith('.mts') || lower.endsWith('.cts') ||
        lower.endsWith('.js') || lower.endsWith('.mjs') || lower.endsWith('.cjs')) {
        return 'typescript';
    }
    return 'unknown';
}

/**
 * Directory variant of {@link classifyAppHostPath}. The Aspire CLI commonly
 * launches AppHosts as a directory (e.g. `aspire run` without `--apphost`)
 * because the entry file lives next to `package.json` / `*.csproj` and is
 * discovered at runtime. Looking only at the directory name itself loses the
 * language signal entirely, so we enumerate the directory and match well-known
 * AppHost file names.
 *
 * Directory reads are O(entries), small for typical AppHost roots; any failure
 * (permissions, missing directory) returns `'unknown'` rather than throwing.
 */
export async function classifyAppHostDirectory(directoryPath: string | undefined): Promise<'csharp' | 'typescript' | 'unknown'> {
    if (!directoryPath) {
        return 'unknown';
    }
    let entries: string[];
    try {
        entries = await fs.readdir(directoryPath);
    }
    catch {
        return 'unknown';
    }
    let sawCsharp = false;
    let sawTypescript = false;
    for (const entry of entries) {
        if (await isCsharpAppHostMarker(directoryPath, entry)) {
            sawCsharp = true;
        }
        else if (isTypescriptAppHostMarker(entry)) {
            sawTypescript = true;
        }
    }
    if (sawCsharp && sawTypescript) {
        // Highly unusual; prefer csharp because Aspire's reference AppHost
        // implementation is csproj-based. Either signal is fine here — the
        // telemetry value is "we recognized it", not "we picked the right one".
        return 'csharp';
    }
    if (sawCsharp) {
        return 'csharp';
    }
    if (sawTypescript) {
        return 'typescript';
    }
    return 'unknown';
}

async function isCsharpAppHostMarker(directoryPath: string, entry: string): Promise<boolean> {
    const lower = entry.toLowerCase();
    if (lower === 'apphost.cs') {
        return true;
    }

    if (!lower.endsWith('.csproj') && !lower.endsWith('.fsproj') && !lower.endsWith('.vbproj')) {
        return false;
    }

    if (projectFileNameLooksLikeAppHost(lower)) {
        return true;
    }

    return await projectFileReferencesAspireAppHost(directoryPath, entry);
}

function projectFileNameLooksLikeAppHost(fileName: string): boolean {
    const nameWithoutExtension = fileName.replace(/\.[^.]+$/, '');
    return nameWithoutExtension === 'apphost'
        || nameWithoutExtension.startsWith('apphost')
        || nameWithoutExtension.endsWith('.apphost')
        || nameWithoutExtension.includes('.apphost.');
}

async function projectFileReferencesAspireAppHost(directoryPath: string, entry: string): Promise<boolean> {
    let contents: string;
    try {
        contents = await fs.readFile(join(directoryPath, entry), 'utf8');
    }
    catch {
        return false;
    }

    const uncommentedContents = contents.replace(/<!--[\s\S]*?-->/g, '');
    // C# AppHost project files can advertise Aspire through SDK, package, or evaluated properties:
    //   <Project Sdk="Aspire.AppHost.Sdk/13.5.0">
    //   <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.0" />
    //   <PackageReference Include="Aspire.Hosting.AppHost" />
    //   <IsAspireHost>true</IsAspireHost>
    return projectSdkReferencesAspireAppHost(uncommentedContents)
        || /<Sdk\b(?=[^>]*\bName\s*=\s*(["'])Aspire\.AppHost\.Sdk\1)[^>]*>/is.test(uncommentedContents)
        || /<(?:PackageReference|AspireProjectOrPackageReference)\b(?=[^>]*\bInclude\s*=\s*["']Aspire\.Hosting(?:\.AppHost)?["'])[^>]*>/is.test(uncommentedContents)
        || /<IsAspireHost>\s*true\s*<\/IsAspireHost>/i.test(uncommentedContents);
}

function projectSdkReferencesAspireAppHost(contents: string): boolean {
    const projectSdkMatch = /<Project\b[^>]*\bSdk\s*=\s*(["'])(?<sdks>.*?)\1/is.exec(contents);
    const sdkAttribute = projectSdkMatch?.groups?.sdks;
    if (!sdkAttribute) {
        return false;
    }

    return sdkAttribute.split(';').some(sdk => /^Aspire\.AppHost\.Sdk(?:\/|$)/i.test(sdk.trim()));
}

function isTypescriptAppHostMarker(entry: string): boolean {
    const lower = entry.toLowerCase();
    return lower === 'apphost.ts' || lower === 'apphost.mts' || lower === 'apphost.cts' ||
        lower === 'apphost.js' || lower === 'apphost.mjs' || lower === 'apphost.cjs';
}
