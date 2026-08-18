import * as vscode from 'vscode';
import { Language, Node as TreeSitterNode, Parser, Tree } from 'web-tree-sitter';
import { AppHostResourceParser, ParsedResource, registerParser } from './AppHostResourceParser';
import { initializeTreeSitter, resolveBundledWasmAssetPath } from './treeSitter';
import { isInInactiveNode, visit } from './treeSitterHelpers';

/**
 * Java AppHost resource parser.
 *
 * Modelled on the C# parser rather than the Rust one: Java spells a resource call
 * `builder.addRedis("cache")`, which parses to a single `method_invocation` carrying `object`,
 * `name` and `arguments` fields, and that is much closer to C#'s `invocation_expression` than to
 * Rust's `call_expression` wrapping a separate `field_expression`.
 */
class JavaAppHostParser implements AppHostResourceParser {
    getSupportedExtensions(): string[] {
        return ['.java'];
    }

    async isAppHostFile(document: vscode.TextDocument): Promise<boolean> {
        return await withJavaTree(document.getText(), tree =>
            findInvocation(tree.rootNode, isCreateBuilderCall) !== undefined);
    }

    async parseResources(document: vscode.TextDocument): Promise<ParsedResource[]> {
        return await withJavaTree(document.getText(), tree => {
            const results: ParsedResource[] = [];
            visit(tree.rootNode, node => {
                if (node.type !== 'method_invocation') {
                    return;
                }

                // A resource call is always invoked on something (`builder.addRedis(...)`), never
                // bare. Requiring the object also skips a local helper that happens to be named
                // `addSomething`.
                if (!node.childForFieldName('object')) {
                    return;
                }

                const methodName = node.childForFieldName('name')?.text;
                if (!methodName || !/^add[A-Z][A-Za-z0-9]*$/.test(methodName)) {
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

                results.push({
                    name: resourceName,
                    methodName,
                    range: new vscode.Range(
                        document.positionAt(getInvocationDotStart(node)),
                        document.positionAt(resourceNameNode.endIndex)),
                    kind: methodName === 'addStep' ? 'pipelineStep' : 'resource',
                    statementStartLine: findContainingStatementStartLine(node),
                });
            });

            return results.sort((left, right) => document.offsetAt(left.range.start) - document.offsetAt(right.range.start));
        });
    }

    async findAppHostEntryPointLine(document: vscode.TextDocument): Promise<number | undefined> {
        return await withJavaTree(document.getText(), tree => findMainMethod(tree.rootNode)?.startPosition.row);
    }

    async findBuilderStatementLine(document: vscode.TextDocument): Promise<number | undefined> {
        return await withJavaTree(document.getText(), tree => {
            const builderInvocation = findInvocation(tree.rootNode, isCreateBuilderCall);
            return builderInvocation ? findContainingStatementStartLine(builderInvocation) : undefined;
        });
    }

    async filterActiveOffsets(document: vscode.TextDocument, offsets: readonly number[]): Promise<number[]> {
        if (offsets.length === 0) {
            return [];
        }

        return await withJavaTree(document.getText(), tree =>
            offsets.filter(offset => !isInInactiveNode(tree.rootNode, offset)));
    }
}

registerParser(new JavaAppHostParser());

let languagePromise: Promise<Language> | undefined;

async function withJavaTree<T>(text: string, callback: (tree: Tree) => T): Promise<T> {
    const language = await getJavaLanguage();
    const parser = new Parser();
    parser.setLanguage(language);

    const tree = parser.parse(text);
    if (!tree) {
        parser.delete();
        throw new Error('Failed to parse Java AppHost document.');
    }

    try {
        return callback(tree);
    }
    finally {
        tree.delete();
        parser.delete();
    }
}

async function getJavaLanguage(): Promise<Language> {
    languagePromise ??= loadJavaLanguage().catch(error => {
        languagePromise = undefined;
        throw error;
    });

    return await languagePromise;
}

async function loadJavaLanguage(): Promise<Language> {
    await initializeTreeSitter();

    return await Language.load(getJavaTreeSitterWasmPath());
}

function getJavaTreeSitterWasmPath(): string {
    const resolvedPath = require.resolve('tree-sitter-java/tree-sitter-java.wasm');
    return typeof resolvedPath === 'string'
        ? resolvedPath
        : resolveBundledWasmAssetPath(require('tree-sitter-java/tree-sitter-java.wasm'));
}

function findInvocation(rootNode: TreeSitterNode, predicate: (node: TreeSitterNode) => boolean): TreeSitterNode | undefined {
    let result: TreeSitterNode | undefined;
    visit(rootNode, node => {
        if (node.type === 'method_invocation' && predicate(node)) {
            result = node;
            return false;
        }

        return true;
    });

    return result;
}

/**
 * Matches `DistributedApplication.CreateBuilder()` and `DistributedApplication.CreateBuilder(args)`,
 * whether the type is imported or written out in full as `aspire.DistributedApplication`.
 * The object is checked as well as the method name so an unrelated local `CreateBuilder()` helper
 * does not make an arbitrary Java file look like an AppHost. Only the segment after the last dot is
 * compared, so a qualified call matches while `ReportBuilder.CreateBuilder()` still does not.
 */
function isCreateBuilderCall(node: TreeSitterNode): boolean {
    if (node.childForFieldName('name')?.text !== 'CreateBuilder') {
        return false;
    }

    const objectText = node.childForFieldName('object')?.text;
    if (objectText === undefined) {
        return false;
    }

    // A qualified name may be split across lines, so the segment is trimmed before it is compared.
    return objectText.slice(objectText.lastIndexOf('.') + 1).trim() === 'DistributedApplication';
}

/**
 * Finds the entry point, covering both shapes an AppHost can take: a JEP 512 implicitly declared
 * `void main()`, and a conventional `public static void main(String[])` inside a class, which is what
 * a Maven or Gradle AppHost project uses. See https://openjdk.org/jeps/512.
 */
function findMainMethod(rootNode: TreeSitterNode): TreeSitterNode | undefined {
    let result: TreeSitterNode | undefined;
    visit(rootNode, node => {
        if (node.type === 'method_declaration' && node.childForFieldName('name')?.text === 'main') {
            result = node;
            return false;
        }

        return true;
    });

    return result;
}

function getFirstArgument(node: TreeSitterNode): TreeSitterNode | undefined {
    const argumentsNode = node.childForFieldName('arguments');
    if (argumentsNode?.hasError) {
        return undefined;
    }

    return argumentsNode?.namedChildren.find(child => !child.isExtra);
}

/**
 * The lens anchors on the `.` before the method name so it lines up with the other languages.
 * `method_invocation` children are `object`, `.`, `name`, `arguments`, so the dot is a direct child.
 */
function getInvocationDotStart(node: TreeSitterNode): number {
    return node.children.find(child => child.type === '.')?.startIndex ?? node.startIndex;
}

/**
 * Java spells a local declaration `local_variable_declaration`, which does not end in `_statement`
 * the way `expression_statement` does, so both are matched explicitly.
 */
function findContainingStatementStartLine(node: TreeSitterNode): number {
    let current: TreeSitterNode | null = node;
    while (current) {
        if (current.type === 'local_variable_declaration' || current.type === 'expression_statement') {
            return current.startPosition.row;
        }

        current = current.parent;
    }

    return node.startPosition.row;
}

/**
 * Reads a Java string literal, e.g. `"catalog"` or `"c:\\tools"`.
 *
 * Text blocks (`"""..."""`) are also `string_literal` in this grammar but are rejected: a resource
 * name never needs one, and treating the opening delimiter as content would silently produce a wrong
 * name. See https://docs.oracle.com/javase/specs/jls/se21/html/jls-3.html#jls-3.10.5.
 */
function getStringLiteralValue(node: TreeSitterNode): string | undefined {
    if (node.hasError || node.type !== 'string_literal') {
        return undefined;
    }

    const text = node.text;
    if (!text.startsWith('"') || text.startsWith('"""') || !text.endsWith('"') || text.length < 2) {
        return undefined;
    }

    let value = '';
    for (const child of node.namedChildren) {
        if (child.type !== 'escape_sequence') {
            value += child.text;
            continue;
        }

        const decoded = decodeEscapeSequence(child.text);
        if (decoded === undefined) {
            return undefined;
        }

        value += decoded;
    }

    return value;
}

/**
 * Decodes the escapes JLS 3.10.7 defines, plus the `\\uXXXX` form. Anything else is rejected rather
 * than passed through, because a name the parser cannot read exactly would anchor a lens to a
 * resource the CLI never reports.
 */
function decodeEscapeSequence(text: string): string | undefined {
    switch (text) {
        case '\\b': return '\b';
        case '\\s': return ' ';
        case '\\t': return '\t';
        case '\\n': return '\n';
        case '\\f': return '\f';
        case '\\r': return '\r';
        case '\\"': return '"';
        case "\\'": return "'";
        case '\\\\': return '\\';
        default: break;
    }

    const unicode = /^\\u+([0-9a-fA-F]{4})$/.exec(text);
    if (unicode) {
        return String.fromCharCode(parseInt(unicode[1], 16));
    }

    const octal = /^\\([0-3]?[0-7]{1,2})$/.exec(text);
    return octal ? String.fromCharCode(parseInt(octal[1], 8)) : undefined;
}
