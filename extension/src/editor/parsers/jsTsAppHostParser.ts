import * as vscode from 'vscode';
import { AppHostResourceParser, ParsedResource, registerParser } from './AppHostResourceParser';

/**
 * JavaScript / TypeScript AppHost resource parser.
 * Detects AppHost files via Aspire module imports, then extracts .add*("name") calls.
 */
class JsTsAppHostParser implements AppHostResourceParser {
    getSupportedExtensions(): string[] {
        return ['.ts', '.js'];
    }

    isAppHostFile(document: vscode.TextDocument): boolean {
        const text = document.getText();
        // Match @aspire package imports, local aspire module imports (e.g. ./.modules/aspire.js),
        // or the createBuilder() entry point from the Aspire TS SDK.
        return /(?:from\s+['"](?:@aspire|[^'"]*aspire[^'"]*)|require\s*\(\s*['"](?:@aspire|[^'"]*aspire[^'"]*))/.test(text)
            || /\bcreateBuilder\s*\(/.test(text);
    }

    parseResources(document: vscode.TextDocument): ParsedResource[] {
        const text = document.getText();
        const results: ParsedResource[] = [];

        // Match .addXyz("name") or .addXyz('name') patterns
        // \s* matches whitespace including newlines, supporting multi-line calls
        const pattern = /\.(add\w+)\s*\(\s*(['"])([^'"]+)\2/gi;
        let match: RegExpExecArray | null;

        while ((match = pattern.exec(text)) !== null) {
            const methodName = match[1];
            const resourceName = match[3];

            const matchStart = match.index;
            const startPos = document.positionAt(matchStart);
            const endPos = document.positionAt(matchStart + match[0].length);

            // Find the start of the full statement (walk back to previous ';', '{', or start of file)
            const statementStartLine = this._findStatementStartLine(text, matchStart, document);

            results.push({
                name: resourceName,
                methodName: methodName,
                range: new vscode.Range(startPos, endPos),
                kind: methodName.toLowerCase() === 'addstep' ? 'pipelineStep' : 'resource',
                statementStartLine,
            });
        }

        return results;
    }

    /**
     * Walk backwards from the match position to find the first line of the statement.
     * Stops at the previous ';', '{', or start of file, then returns the first non-comment,
     * non-blank line after that delimiter.
     */
    private _findStatementStartLine(text: string, matchIndex: number, document: vscode.TextDocument): number {
        let i = matchIndex - 1;
        while (i >= 0) {
            const ch = text[i];
            if (ch === ';' || ch === '{') {
                break;
            }
            i--;
        }
        let start = i + 1;
        while (start < matchIndex && /\s/.test(text[start])) {
            start++;
        }
        let line = document.positionAt(start).line;
        const matchLine = document.positionAt(matchIndex).line;
        // Skip lines that are comments (// or /* or * continuation)
        while (line < matchLine) {
            const lineText = document.lineAt(line).text.trimStart();
            if (lineText.startsWith('//') || lineText.startsWith('/*') || lineText.startsWith('*')) {
                line++;
            } else {
                break;
            }
        }
        return line;
    }
}

// Self-register on import
registerParser(new JsTsAppHostParser());
