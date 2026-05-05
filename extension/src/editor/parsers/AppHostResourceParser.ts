import * as vscode from 'vscode';

/**
 * Represents a single resource definition found in an AppHost file.
 */
export interface ParsedResource {
    /** The resource name passed to the Add* method, e.g. "cache" in AddRedis("cache"). */
    name: string;
    /** The method name that added the resource, e.g. "AddRedis". */
    methodName: string;
    /** The range in the document covering the Add*("name") call. */
    range: vscode.Range;
    /** Whether this is a resource declaration or pipeline step. */
    kind: 'resource' | 'pipelineStep';
    /** The line number of the first line of the full statement chain (e.g. the variable declaration line for a multi-line fluent chain). */
    statementStartLine?: number;
}

/**
 * Language-specific parser that can detect AppHost files and extract resource definitions.
 */
export interface AppHostResourceParser {
    /** File extensions this parser handles, e.g. [".cs"]. */
    getSupportedExtensions(): string[];

    /** Returns true if the given document is an AppHost file for this language. */
    isAppHostFile(document: vscode.TextDocument): boolean;

    /** Parse resource definitions from the document. */
    parseResources(document: vscode.TextDocument): ParsedResource[];

    /**
     * Locates the line containing the builder construction statement
     * (e.g. `var builder = DistributedApplication.CreateBuilder(args);` for C#,
     * `const builder = createBuilder();` for TS/JS).
     * Returns the 0-based line of the start of the statement, or `undefined` if not found.
     */
    findBuilderStatementLine?(document: vscode.TextDocument): number | undefined;
}

const _parsers: AppHostResourceParser[] = [];

export function registerParser(parser: AppHostResourceParser): void {
    _parsers.push(parser);
}

export function getParserForDocument(document: vscode.TextDocument): AppHostResourceParser | undefined {
    const ext = getFileExtension(document.uri.fsPath);
    return _parsers.find(p => p.getSupportedExtensions().includes(ext) && p.isAppHostFile(document));
}

export function getAllParsers(): readonly AppHostResourceParser[] {
    return _parsers;
}

export function getSupportedLanguageIds(): string[] {
    const ids = new Set<string>();
    for (const parser of _parsers) {
        for (const ext of parser.getSupportedExtensions()) {
            const langId = extensionToLanguageId(ext);
            if (langId) {
                ids.add(langId);
            }
        }
    }
    return [...ids];
}

function getFileExtension(filePath: string): string {
    const lastDot = filePath.lastIndexOf('.');
    return lastDot >= 0 ? filePath.substring(lastDot).toLowerCase() : '';
}

function extensionToLanguageId(ext: string): string | undefined {
    switch (ext) {
        case '.cs': return 'csharp';
        case '.ts': return 'typescript';
        case '.js': return 'javascript';
        default: return undefined;
    }
}
