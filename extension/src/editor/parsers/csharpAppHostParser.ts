import * as path from 'path';
import * as vscode from 'vscode';
import { Language, Node as TreeSitterNode, Parser, Tree } from 'web-tree-sitter';
import { AppHostResourceParser, ParsedResource, registerParser } from './AppHostResourceParser';

/**
 * C# AppHost resource parser.
 * Detects AppHost files via SDK directive or DistributedApplication.CreateBuilder pattern,
 * then extracts builder.Add*("name") calls.
 */
class CSharpAppHostParser implements AppHostResourceParser {
    getSupportedExtensions(): string[] {
        return ['.cs'];
    }

    async isAppHostFile(document: vscode.TextDocument): Promise<boolean> {
        const text = document.getText();
        return await withCSharpTree(text, tree =>
            hasActiveSdkDirective(text, tree.rootNode)
            || findInvocation(tree.rootNode, isDistributedApplicationCreateBuilderCall) !== undefined);
    }

    async parseResources(document: vscode.TextDocument): Promise<ParsedResource[]> {
        const text = document.getText();
        return await withCSharpTree(text, tree => {
            const results: ParsedResource[] = [];
            visit(tree.rootNode, node => {
                if (!isInvocationExpression(node)) {
                    return;
                }

                const memberAccess = getInvocationMemberAccess(node);
                if (!memberAccess) {
                    return;
                }

                const methodName = getMemberName(memberAccess);
                if (!methodName || !/^Add\w+$/.test(methodName)) {
                    return;
                }

                const resourceNameNode = getFirstArgumentExpression(node);
                if (!resourceNameNode) {
                    return;
                }

                const resourceName = getStringLiteralValue(resourceNameNode);
                if (resourceName === undefined) {
                    return;
                }

                const matchStart = getMemberAccessDotStart(memberAccess);
                const startPos = document.positionAt(matchStart);
                const endPos = document.positionAt(resourceNameNode.endIndex);
                results.push({
                    name: resourceName,
                    methodName,
                    range: new vscode.Range(startPos, endPos),
                    kind: methodName === 'AddStep' ? 'pipelineStep' : 'resource',
                    statementStartLine: findContainingStatementStartLine(node),
                });
            });

            return results.sort((a, b) => document.offsetAt(a.range.start) - document.offsetAt(b.range.start));
        });
    }

    async findBuilderStatementLine(document: vscode.TextDocument): Promise<number | undefined> {
        const text = document.getText();
        return await withCSharpTree(text, tree => {
            const builderInvocation = findInvocation(tree.rootNode, isDistributedApplicationCreateBuilderCall);
            return builderInvocation ? findContainingStatementStartLine(builderInvocation) : undefined;
        });
    }

}

// Self-register on import
registerParser(new CSharpAppHostParser());

let languagePromise: Promise<Language> | undefined;

async function withCSharpTree<T>(text: string, callback: (tree: Tree) => T): Promise<T> {
    const language = await getCSharpLanguage();
    const parser = new Parser();
    parser.setLanguage(language);

    const tree = parser.parse(text);
    if (!tree) {
        parser.delete();
        throw new Error('Failed to parse C# AppHost document.');
    }

    try {
        return callback(tree);
    } finally {
        tree.delete();
        parser.delete();
    }
}

async function getCSharpLanguage(): Promise<Language> {
    languagePromise ??= loadCSharpLanguage().catch(error => {
        languagePromise = undefined;
        throw error;
    });

    return await languagePromise;
}

async function loadCSharpLanguage(): Promise<Language> {
    await Parser.init({
        locateFile: () => getWebTreeSitterWasmPath(),
    });

    return await Language.load(getCSharpTreeSitterWasmPath());
}

function getWebTreeSitterWasmPath(): string {
    const resolvedPath = require.resolve('web-tree-sitter/web-tree-sitter.wasm');
    return typeof resolvedPath === 'string'
        ? resolvedPath
        : resolveBundledWasmAssetPath(require('web-tree-sitter/web-tree-sitter.wasm'));
}

function getCSharpTreeSitterWasmPath(): string {
    const resolvedPath = require.resolve('tree-sitter-c-sharp/tree-sitter-c_sharp.wasm');
    return typeof resolvedPath === 'string'
        ? resolvedPath
        : resolveBundledWasmAssetPath(require('tree-sitter-c-sharp/tree-sitter-c_sharp.wasm'));
}

function resolveBundledWasmAssetPath(assetPath: string): string {
    return path.isAbsolute(assetPath) ? assetPath : path.join(__dirname, assetPath);
}

function hasActiveSdkDirective(text: string, rootNode: TreeSitterNode): boolean {
    const pattern = /^[ \t]*#:sdk[ \t]+Aspire\.AppHost\.Sdk\b/gm;
    let match: RegExpExecArray | null;
    while ((match = pattern.exec(text)) !== null) {
        if (!isInInactiveNode(rootNode, match.index)) {
            return true;
        }
    }

    return false;
}

function isInInactiveNode(rootNode: TreeSitterNode, index: number): boolean {
    let node = rootNode.descendantForIndex(index);
    while (node) {
        if (node.type === 'comment' || node.type.includes('string')) {
            return true;
        }

        node = node.parent;
    }

    return false;
}

function findInvocation(rootNode: TreeSitterNode, predicate: (node: TreeSitterNode) => boolean): TreeSitterNode | undefined {
    let result: TreeSitterNode | undefined;
    visit(rootNode, node => {
        if (isInvocationExpression(node) && predicate(node)) {
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

function isInvocationExpression(node: TreeSitterNode): boolean {
    return node.type === 'invocation_expression';
}

function isDistributedApplicationCreateBuilderCall(node: TreeSitterNode): boolean {
    const memberAccess = getInvocationMemberAccess(node);
    return memberAccess !== undefined
        && getMemberName(memberAccess) === 'CreateBuilder'
        && getMemberExpressionText(memberAccess).endsWith('DistributedApplication');
}

function getInvocationMemberAccess(invocation: TreeSitterNode): TreeSitterNode | undefined {
    const functionNode = invocation.childForFieldName('function');
    return functionNode?.type === 'member_access_expression' ? functionNode : undefined;
}

function getMemberName(memberAccess: TreeSitterNode): string | undefined {
    const nameNode = memberAccess.childForFieldName('name');
    if (!nameNode) {
        return undefined;
    }

    if (nameNode.type === 'identifier') {
        return nameNode.text;
    }

    if (nameNode.type === 'generic_name') {
        return nameNode.namedChildren.find(child => child.type === 'identifier')?.text;
    }

    return undefined;
}

function getMemberExpressionText(memberAccess: TreeSitterNode): string {
    return memberAccess.childForFieldName('expression')?.text ?? '';
}

function getMemberAccessDotStart(memberAccess: TreeSitterNode): number {
    const nameNode = memberAccess.childForFieldName('name');
    return nameNode ? nameNode.startIndex - 1 : memberAccess.startIndex;
}

function getFirstArgumentExpression(invocation: TreeSitterNode): TreeSitterNode | undefined {
    const argumentList = invocation.childForFieldName('arguments');
    const firstArgument = argumentList?.namedChildren.find(child => child.type === 'argument');
    return firstArgument?.namedChildren[0];
}

function getStringLiteralValue(node: TreeSitterNode): string | undefined {
    if (node.type === 'string_literal') {
        return node.namedChildren
            .map(child => child.type === 'string_literal_content' ? child.text : decodeEscapeSequence(child.text))
            .join('');
    }

    if (node.type === 'verbatim_string_literal') {
        return node.text.slice(2, -1).replaceAll('""', '"');
    }

    return undefined;
}

function decodeEscapeSequence(text: string): string {
    switch (text) {
        case '\\"': return '"';
        case '\\\\': return '\\';
        case '\\n': return '\n';
        case '\\r': return '\r';
        case '\\t': return '\t';
        default: return text;
    }
}

function findContainingStatementStartLine(node: TreeSitterNode): number {
    let current: TreeSitterNode | null = node;
    while (current) {
        // Partially-typed code can make tree-sitter wrap a valid resource call in an
        // incomplete earlier statement, e.g. `if (env) { ... }\nvar cache = builder\n    .AddRedis("cache")`.
        // In that shape the statement's start points at stale malformed code rather
        // than the resource declaration, so skip statements with preceding ERROR nodes.
        if (current.type.endsWith('_statement') && !hasErrorBeforeNode(current, node.startIndex)) {
            return current.startPosition.row;
        }

        current = current.parent;
    }

    return node.startPosition.row;
}

function hasErrorBeforeNode(node: TreeSitterNode, nodeStartIndex: number): boolean {
    let result = false;
    visit(node, child => {
        if (child.startIndex >= nodeStartIndex) {
            return false;
        }

        if (child.type === 'ERROR' && child.endIndex <= nodeStartIndex) {
            result = true;
            return false;
        }

        return true;
    });

    return result;
}
