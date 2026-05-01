import * as vscode from 'vscode';
import { AppHostResourceParser, ParsedResource, registerParser } from './AppHostResourceParser';
import { findStatementStartLine, findFirstMatchOutsideComments } from './parserUtils';

/**
 * C# AppHost resource parser.
 * Detects AppHost files via SDK directive or DistributedApplication.CreateBuilder pattern,
 * then extracts builder.Add*("name") calls.
 */
class CSharpAppHostParser implements AppHostResourceParser {
    getSupportedExtensions(): string[] {
        return ['.cs'];
    }

    isAppHostFile(document: vscode.TextDocument): boolean {
        const text = document.getText();
        if (text.includes('#:sdk Aspire.AppHost.Sdk')) {
            return true;
        }
        return text.includes('DistributedApplication.CreateBuilder');
    }

    parseResources(document: vscode.TextDocument): ParsedResource[] {
        const text = document.getText();
        const results: ParsedResource[] = [];

        // Match .AddXyz("name") or .AddXyz<...>("name") patterns
        const addPattern = /\.(Add\w+)(?:<[^>]*>)?\s*\(\s*"([^"]+)"/g;
        let match: RegExpExecArray | null;

        while ((match = addPattern.exec(text)) !== null) {
            const methodName = match[1];
            const resourceName = match[2];
            const matchStart = match.index;
            const startPos = document.positionAt(matchStart);
            const endPos = document.positionAt(matchStart + match[0].length);

            // Find the start of the full statement (walk back to previous ';', '{', '}', or start of file)
            const statementStartLine = findStatementStartLine(text, matchStart, document);

            results.push({
                name: resourceName,
                methodName: methodName,
                range: new vscode.Range(startPos, endPos),
                kind: methodName === 'AddStep' ? 'pipelineStep' : 'resource',
                statementStartLine,
            });
        }

        return results;
    }

    findBuilderStatementLine(document: vscode.TextDocument): number | undefined {
        const text = document.getText();
        const match = findFirstMatchOutsideComments(text, /\bDistributedApplication\.CreateBuilder\b/g, document);
        if (!match) {
            return undefined;
        }
        return findStatementStartLine(text, match.index, document);
    }

}

// Self-register on import
registerParser(new CSharpAppHostParser());
