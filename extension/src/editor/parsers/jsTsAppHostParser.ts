import * as vscode from 'vscode';
import * as ts from 'typescript';
import { AppHostResourceParser, ParsedResource, registerParser } from './AppHostResourceParser';

/**
 * JavaScript / TypeScript AppHost resource parser.
 * Detects AppHost files via Aspire module imports, then extracts .add*("name") calls.
 */
class JsTsAppHostParser implements AppHostResourceParser {
    getSupportedExtensions(): string[] {
        return ['.ts', '.js'];
    }

    async isAppHostFile(document: vscode.TextDocument): Promise<boolean> {
        const sourceFile = createSourceFile(document);
        let isAppHost = false;

        visit(sourceFile, node => {
            if (ts.isImportDeclaration(node)
                && ts.isStringLiteral(node.moduleSpecifier)
                && isAspireModuleSpecifier(node.moduleSpecifier.text)) {
                isAppHost = true;
                return false;
            }

            if (ts.isCallExpression(node)
                && (isAspireRequireCall(node) || isCreateBuilderCall(node))) {
                isAppHost = true;
                return false;
            }

            return true;
        });

        return isAppHost;
    }

    async parseResources(document: vscode.TextDocument): Promise<ParsedResource[]> {
        const sourceFile = createSourceFile(document);
        const results: ParsedResourceWithStart[] = [];

        visit(sourceFile, node => {
            if (!ts.isCallExpression(node) || !ts.isPropertyAccessExpression(node.expression)) {
                return true;
            }

            const methodName = node.expression.name.text;
            if (!/^add\w+$/i.test(methodName)) {
                return true;
            }

            const firstArgument = node.arguments[0];
            if (!firstArgument || !ts.isStringLiteral(firstArgument) || !isClosedStringLiteral(sourceFile, firstArgument)) {
                return true;
            }

            const matchStart = getPropertyAccessStart(sourceFile, node.expression);
            const startPos = document.positionAt(matchStart);
            const endPos = document.positionAt(firstArgument.getEnd());
            const statementStartLine = findContainingStatementStartLine(sourceFile, node, document);

            results.push({
                name: firstArgument.text,
                methodName: methodName,
                range: new vscode.Range(startPos, endPos),
                kind: methodName.toLowerCase() === 'addstep' ? 'pipelineStep' : 'resource',
                statementStartLine,
                matchStart,
            });

            return true;
        });

        return results
            .sort((left, right) => left.matchStart - right.matchStart)
            .map(({ matchStart: _, ...resource }) => resource);
    }

    async findBuilderStatementLine(document: vscode.TextDocument): Promise<number | undefined> {
        const sourceFile = createSourceFile(document);
        let builderLine: number | undefined;

        visit(sourceFile, node => {
            if (!ts.isCallExpression(node) || !isCreateBuilderCall(node)) {
                return true;
            }

            builderLine = findContainingStatementStartLine(sourceFile, node, document);
            return false;
        });

        return builderLine;
    }

}

// Self-register on import
registerParser(new JsTsAppHostParser());

interface ParsedResourceWithStart extends ParsedResource {
    matchStart: number;
}

function createSourceFile(document: vscode.TextDocument): ts.SourceFile {
    return ts.createSourceFile(
        document.uri.fsPath,
        document.getText(),
        ts.ScriptTarget.Latest,
        true,
        document.uri.fsPath.endsWith('.js') ? ts.ScriptKind.JS : ts.ScriptKind.TS
    );
}

function visit(node: ts.Node, visitor: (node: ts.Node) => boolean): boolean {
    if (!visitor(node)) {
        return false;
    }

    let keepGoing = true;
    ts.forEachChild(node, child => {
        if (keepGoing) {
            keepGoing = visit(child, visitor);
        }
    });

    return keepGoing;
}

function isAspireModuleSpecifier(moduleName: string): boolean {
    return moduleName.startsWith('@aspire') || moduleName.includes('aspire');
}

function isAspireRequireCall(node: ts.CallExpression): boolean {
    return ts.isIdentifier(node.expression)
        && node.expression.text === 'require'
        && node.arguments.length > 0
        && ts.isStringLiteral(node.arguments[0])
        && isAspireModuleSpecifier(node.arguments[0].text);
}

function isCreateBuilderCall(node: ts.CallExpression): boolean {
    if (ts.isIdentifier(node.expression)) {
        return node.expression.text === 'createBuilder';
    }

    return ts.isPropertyAccessExpression(node.expression)
        && node.expression.name.text === 'createBuilder';
}

function getPropertyAccessStart(sourceFile: ts.SourceFile, node: ts.PropertyAccessExpression): number {
    const nameStart = node.name.getStart(sourceFile);
    return sourceFile.text[nameStart - 1] === '.' ? nameStart - 1 : nameStart;
}

function isClosedStringLiteral(sourceFile: ts.SourceFile, node: ts.StringLiteral): boolean {
    const literalText = node.getText(sourceFile);
    const quote = literalText[0];
    return (quote === '"' || quote === "'") && literalText.length > 1 && literalText[literalText.length - 1] === quote;
}

function findContainingStatementStartLine(sourceFile: ts.SourceFile, node: ts.Node, document: vscode.TextDocument): number {
    let current: ts.Node | undefined = node;
    while (current) {
        if (ts.isStatement(current)) {
            return document.positionAt(current.getStart(sourceFile)).line;
        }

        current = current.parent;
    }

    return document.positionAt(node.getStart(sourceFile)).line;
}
