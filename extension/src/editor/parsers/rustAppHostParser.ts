import * as vscode from 'vscode';
import { Language, Node as TreeSitterNode, Parser, Tree } from 'web-tree-sitter';
import { AppHostResourceParser, ParsedResource, registerParser } from './AppHostResourceParser';
import { initializeTreeSitter, resolveBundledWasmAssetPath } from './treeSitter';
import { isInInactiveNode, visit } from './treeSitterHelpers';

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
            findCall(tree.rootNode, isCreateBuilderCall) !== undefined);
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

    async findAppHostEntryPointLine(document: vscode.TextDocument): Promise<number | undefined> {
        return await withRustTree(document.getText(), tree => findMainFunction(tree.rootNode)?.startPosition.row);
    }

    async findBuilderStatementLine(document: vscode.TextDocument): Promise<number | undefined> {
        return await withRustTree(document.getText(), tree => {
            const builderCall = findCall(tree.rootNode, isCreateBuilderCall);
            return builderCall ? findContainingStatementStartLine(builderCall) : undefined;
        });
    }

    async filterActiveOffsets(document: vscode.TextDocument, offsets: readonly number[]): Promise<number[]> {
        if (offsets.length === 0) {
            return [];
        }

        return await withRustTree(document.getText(), tree =>
            offsets.filter(offset => !isInInactiveNode(tree.rootNode, offset)));
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
    await initializeTreeSitter();

    return await Language.load(getRustTreeSitterWasmPath());
}

function getRustTreeSitterWasmPath(): string {
    const resolvedPath = require.resolve('tree-sitter-rust/tree-sitter-rust.wasm');
    return typeof resolvedPath === 'string'
        ? resolvedPath
        : resolveBundledWasmAssetPath(require('tree-sitter-rust/tree-sitter-rust.wasm'));
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

function findMainFunction(rootNode: TreeSitterNode): TreeSitterNode | undefined {
    let result: TreeSitterNode | undefined;
    visit(rootNode, node => {
        if (node.type === 'function_item' && node.childForFieldName('name')?.text === 'main') {
            result = node;
            return false;
        }

        return true;
    });

    return result;
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

function isCreateBuilderCall(call: TreeSitterNode): boolean {
    const functionNode = call.childForFieldName('function');
    return (functionNode?.type === 'identifier' || functionNode?.type === 'scoped_identifier')
        && getCallName(call) === 'create_builder';
}

function getCallMemberAccess(call: TreeSitterNode): TreeSitterNode | undefined {
    // A turbofish such as `builder.add_project::<Frontend>("web")` parses as
    //   call_expression -> function: generic_function -> function: field_expression + type_arguments
    // so the member access has to be unwrapped from the generic_function before the `add_*` field
    // name can be read. Plain `builder.add_redis("cache")` calls have the field_expression directly.
    // See the `generic_function` rule in https://github.com/tree-sitter/tree-sitter-rust/blob/master/grammar.js.
    const functionNode = call.childForFieldName('function');
    const memberAccess = functionNode?.type === 'generic_function'
        ? functionNode.childForFieldName('function')
        : functionNode;

    return memberAccess?.type === 'field_expression' ? memberAccess : undefined;
}

function getFirstArgument(call: TreeSitterNode): TreeSitterNode | undefined {
    const argumentsNode = call.childForFieldName('arguments');
    if (argumentsNode?.hasError) {
        return undefined;
    }

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

        let value = '';
        let trimContinuationWhitespace = false;
        for (const child of node.namedChildren) {
            if (child.type !== 'escape_sequence') {
                value += trimContinuationWhitespace ? child.text.replace(/^[ \t\r\n]*/, '') : child.text;
                trimContinuationWhitespace = false;
                continue;
            }

            const decoded = decodeEscapeSequence(child.text);
            if (decoded === undefined) {
                return undefined;
            }

            value += decoded;
            trimContinuationWhitespace = /^\\\r?\n$/.test(child.text);
        }

        return value;
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

function decodeEscapeSequence(text: string): string | undefined {
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
        const byteValue = Number.parseInt(asciiEscape, 16);
        return byteValue <= 0x7F ? String.fromCharCode(byteValue) : undefined;
    }

    const unicodeEscape = /^\\u\{(?<value>[0-9a-fA-F_]{1,7})\}$/.exec(text)?.groups?.value;
    if (unicodeEscape) {
        const normalizedValue = unicodeEscape.replaceAll('_', '');
        const codePoint = Number.parseInt(normalizedValue, 16);
        if (normalizedValue.length === 0 || !Number.isInteger(codePoint) || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF)) {
            return undefined;
        }

        return String.fromCodePoint(codePoint);
    }

    return /^\\\r?\n$/.test(text) ? '' : undefined;
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