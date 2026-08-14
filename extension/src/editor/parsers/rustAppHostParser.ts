import * as path from 'path';
import * as vscode from 'vscode';
import { Language, Node as TreeSitterNode, Parser, Tree } from 'web-tree-sitter';
import { AppHostResourceParser, ParsedResource, registerParser } from './AppHostResourceParser';

/**
 * Rust AppHost resource parser.
 * Detects AppHost files through create_builder calls and extracts add_* resource calls.
 */
class RustAppHostParser implements AppHostResourceParser {
    getSupportedExtensions(): string[] {
        return ['.rs'];
    }

    async isAppHostFile(document: vscode.TextDocument): Promise<boolean> {
        return await withRustTree(document.getText(), tree =>
            findCall(tree.rootNode, node => getCallName(node) === 'create_builder') !== undefined);
    }

    async parseResources(document: vscode.TextDocument): Promise<ParsedResource[]> {
        return await withRustTree(document.getText(), tree => {
            const results: ParsedResource[] = [];
            visit(tree.rootNode, node => {
                if (node.type !== 'call_expression') {
                    return;
                }

                const memberAccess = getCallMemberAccess(node);
                const methodName = memberAccess?.childForFieldName('field')?.text;
                if (!methodName || !/^add_[a-zA-Z0-9_]+$/.test(methodName)) {
                    return;
                }

                const resourceNameNode = getFirstArgument(node);
                if (!resourceNameNode) {
                    return;
                }

                const resourceName = getStringLiteralValue(resourceNameNode);
                if (resourceName === undefined) {
                    return;
                }

                const memberStart = getMemberAccessDotStart(memberAccess);
                results.push({
                    name: resourceName,
                    methodName,
                    range: new vscode.Range(document.positionAt(memberStart), document.positionAt(resourceNameNode.endIndex)),
                    kind: methodName === 'add_step' ? 'pipelineStep' : 'resource',
                    statementStartLine: findContainingStatementStartLine(node),
                });
            });

            return results.sort((left, right) => document.offsetAt(left.range.start) - document.offsetAt(right.range.start));
        });
    }

    async findBuilderStatementLine(document: vscode.TextDocument): Promise<number | undefined> {
        return await withRustTree(document.getText(), tree => {
            const builderCall = findCall(tree.rootNode, node => getCallName(node) === 'create_builder');
            return builderCall ? findContainingStatementStartLine(builderCall) : undefined;
        });
    }
}

registerParser(new RustAppHostParser());

let languagePromise: Promise<Language> | undefined;

async function withRustTree<T>(text: string, callback: (tree: Tree) => T): Promise<T> {
    const language = await getRustLanguage();
    const parser = new Parser();
    parser.setLanguage(language);

    const tree = parser.parse(text);
    if (!tree) {
        parser.delete();
        throw new Error('Failed to parse Rust AppHost document.');
    }

    try {
        return callback(tree);
    }
    finally {
        tree.delete();
        parser.delete();
    }
}

async function getRustLanguage(): Promise<Language> {
    languagePromise ??= loadRustLanguage().catch(error => {
        languagePromise = undefined;
        throw error;
    });

    return await languagePromise;
}

async function loadRustLanguage(): Promise<Language> {
    await Parser.init({
        locateFile: () => getWebTreeSitterWasmPath(),
    });

    return await Language.load(getRustTreeSitterWasmPath());
}

function getWebTreeSitterWasmPath(): string {
    const resolvedPath = require.resolve('web-tree-sitter/web-tree-sitter.wasm');
    return typeof resolvedPath === 'string'
        ? resolvedPath
        : resolveBundledWasmAssetPath(require('web-tree-sitter/web-tree-sitter.wasm'));
}

function getRustTreeSitterWasmPath(): string {
    const resolvedPath = require.resolve('tree-sitter-rust/tree-sitter-rust.wasm');
    return typeof resolvedPath === 'string'
        ? resolvedPath
        : resolveBundledWasmAssetPath(require('tree-sitter-rust/tree-sitter-rust.wasm'));
}

function resolveBundledWasmAssetPath(assetPath: string): string {
    return path.isAbsolute(assetPath) ? assetPath : path.join(__dirname, assetPath);
}

function findCall(rootNode: TreeSitterNode, predicate: (node: TreeSitterNode) => boolean): TreeSitterNode | undefined {
    let result: TreeSitterNode | undefined;
    visit(rootNode, node => {
        if (node.type === 'call_expression' && predicate(node)) {
            result = node;
            return false;
        }

        return true;
    });

    return result;
}

function visit(node: TreeSitterNode, visitor: (node: TreeSitterNode) => boolean | void): boolean {
    if (visitor(node) === false) {
        return false;
    }

    for (const child of node.namedChildren) {
        if (!visit(child, visitor)) {
            return false;
        }
    }

    return true;
}

function getCallName(call: TreeSitterNode): string | undefined {
    const functionNode = call.childForFieldName('function');
    if (functionNode?.type === 'identifier') {
        return functionNode.text;
    }

    if (functionNode?.type === 'scoped_identifier') {
        return functionNode.childForFieldName('name')?.text;
    }

    if (functionNode?.type === 'field_expression') {
        return functionNode.childForFieldName('field')?.text;
    }

    return undefined;
}

function getCallMemberAccess(call: TreeSitterNode): TreeSitterNode | undefined {
    const functionNode = call.childForFieldName('function');
    return functionNode?.type === 'field_expression' ? functionNode : undefined;
}

function getFirstArgument(call: TreeSitterNode): TreeSitterNode | undefined {
    const argumentsNode = call.childForFieldName('arguments');
    return argumentsNode?.namedChildren.find(child => !child.isExtra);
}

function getMemberAccessDotStart(memberAccess: TreeSitterNode): number {
    return memberAccess.children.find(child => child.type === '.')?.startIndex ?? memberAccess.startIndex;
}

function getStringLiteralValue(node: TreeSitterNode): string | undefined {
    if (node.hasError) {
        return undefined;
    }

    if (node.type === 'string_literal') {
        if (!node.text.startsWith('"') || !node.text.endsWith('"')) {
            return undefined;
        }

        return node.namedChildren
            .map(child => child.type === 'escape_sequence' ? decodeEscapeSequence(child.text) : child.text)
            .join('');
    }

    if (node.type === 'raw_string_literal') {
        return getRawStringValue(node.text);
    }

    return undefined;
}

function getRawStringValue(text: string): string | undefined {
    if (!text.startsWith('r')) {
        return undefined;
    }

    const openingQuote = text.indexOf('"');
    if (openingQuote < 1) {
        return undefined;
    }

    const hashes = text.slice(1, openingQuote);
    if ([...hashes].some(character => character !== '#')) {
        return undefined;
    }

    const closingDelimiter = `"${hashes}`;
    return text.endsWith(closingDelimiter)
        ? text.slice(openingQuote + 1, -closingDelimiter.length)
        : undefined;
}

function decodeEscapeSequence(text: string): string {
    switch (text) {
        case '\\0': return '\0';
        case '\\t': return '\t';
        case '\\n': return '\n';
        case '\\r': return '\r';
        case '\\"': return '"';
        case "\\'": return "'";
        case '\\\\': return '\\';
    }

    const asciiEscape = /^\\x(?<value>[0-9a-fA-F]{2})$/.exec(text)?.groups?.value;
    if (asciiEscape) {
        return String.fromCharCode(Number.parseInt(asciiEscape, 16));
    }

    const unicodeEscape = /^\\u\{(?<value>[0-9a-fA-F_]{1,7})\}$/.exec(text)?.groups?.value;
    if (unicodeEscape) {
        return String.fromCodePoint(Number.parseInt(unicodeEscape.replaceAll('_', ''), 16));
    }

    return /^\\\r?\n[ \t]*$/.test(text) ? '' : text;
}

function findContainingStatementStartLine(node: TreeSitterNode): number {
    let current: TreeSitterNode | null = node;
    while (current) {
        if (current.type === 'let_declaration' || current.type === 'expression_statement') {
            return current.startPosition.row;
        }

        current = current.parent;
    }

    return node.startPosition.row;
}