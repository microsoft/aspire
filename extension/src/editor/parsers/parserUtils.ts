import * as vscode from 'vscode';

/**
 * Walk backwards from the match position to find the first line of the statement.
 * Stops at the previous ';', '{', or start of file, then returns the first non-comment,
 * non-blank line after that delimiter. When a '}' is encountered, the matched '{...}'
 * block is inspected: if preceded by '=>' it is a lambda body within the current fluent
 * chain and is skipped; otherwise the '}' is treated as a statement boundary.
 *
 * Shared by C# and JS/TS AppHost parsers since the statement-boundary rules are
 * identical for C-syntax languages.
 */
export function findStatementStartLine(text: string, matchIndex: number, document: vscode.TextDocument): number {
    let i = matchIndex - 1;
    while (i >= 0) {
        const ch = text[i];
        if (ch === ';' || ch === '{') {
            break;
        }
        if (ch === '}') {
            const openBraceIdx = findMatchingOpenBrace(text, i);
            if (openBraceIdx < 0) {
                // No matching open brace — treat as delimiter
                break;
            }
            if (isPrecededByArrow(text, openBraceIdx)) {
                // Lambda body in the current fluent chain — skip over it
                i = openBraceIdx - 1;
                continue;
            }
            // Separate statement block — treat '}' as delimiter
            break;
        }
        i--;
    }
    // i is now at the delimiter or -1 (start of file)
    // Find the first non-whitespace character after the delimiter
    let start = i + 1;
    while (start < matchIndex && /\s/.test(text[start])) {
        start++;
    }
    let line = document.positionAt(start).line;
    const matchLine = document.positionAt(matchIndex).line;
    // Skip lines that are only closing braces (with optional comment), comments,
    // C# 12 file-scoped top-level directives (e.g. `#:sdk Aspire.AppHost.Sdk`),
    // or blank lines.
    while (line < matchLine) {
        const lineText = document.lineAt(line).text.trimStart();
        if (lineText === ''
            || /^\}\s*(\/\/.*)?$/.test(lineText)
            || lineText.startsWith('//')
            || lineText.startsWith('/*')
            || lineText.startsWith('*')
            || lineText.startsWith('#:')) {
            line++;
        } else {
            break;
        }
    }
    return line;
}

/**
 * Find the first match of `regex` whose line is not a comment line (// or /* / *).
 * Used to avoid matching builder-detection patterns inside header/block comments.
 * The regex must be created with the global flag.
 */
export function findFirstMatchOutsideComments(text: string, regex: RegExp, document: vscode.TextDocument): RegExpExecArray | undefined {
    if (!regex.global) {
        throw new Error('findFirstMatchOutsideComments requires a global regex');
    }
    let match: RegExpExecArray | null;
    while ((match = regex.exec(text)) !== null) {
        const line = document.positionAt(match.index).line;
        const lineText = document.lineAt(line).text.trimStart();
        if (lineText.startsWith('//') || lineText.startsWith('/*') || lineText.startsWith('*')) {
            continue;
        }
        return match;
    }
    return undefined;
}

/**
 * Starting from a '}' at closeBraceIdx, walk backwards to find the matching '{'.
 * Returns the index of '{', or -1 if not found.
 */
export function findMatchingOpenBrace(text: string, closeBraceIdx: number): number {
    let depth = 1;
    let j = closeBraceIdx - 1;
    while (j >= 0 && depth > 0) {
        if (text[j] === '}') {
            depth++;
        } else if (text[j] === '{') {
            depth--;
        }
        j--;
    }
    return depth === 0 ? j + 1 : -1;
}

/**
 * Check whether the '{' at openBraceIdx is preceded (ignoring whitespace) by '=>'.
 */
export function isPrecededByArrow(text: string, openBraceIdx: number): boolean {
    let k = openBraceIdx - 1;
    while (k >= 0 && /\s/.test(text[k])) {
        k--;
    }
    return k >= 1 && text[k - 1] === '=' && text[k] === '>';
}
