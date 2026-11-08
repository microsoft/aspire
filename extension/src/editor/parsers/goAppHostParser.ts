import * as vscode from 'vscode';
import { AppHostResourceParser, ParsedResource, registerParser } from './AppHostResourceParser';
import { filterActiveOffsetsInPlainText } from './plainTextInactiveOffsets';

interface Token {
    text: string;
    start: number;
    end: number;
}

// Keep comments, interpreted strings, raw strings and rune literals atomic so examples inside them
// cannot become calls. Offsets remain UTF-16 document offsets, including non-ASCII source text.
// Go resource declarations look like: cache := builder.AddRedis("cache").WithDataVolume()
// See https://go.dev/ref/spec#Tokens and https://go.dev/ref/spec#String_literals.
function tokenize(text: string): Token[] {
    return [...text.matchAll(/\/\/[^\r\n]*|\/\*[\s\S]*?(?:\*\/|$)|"(?:\\[^\r\n]|[^"\\\r\n])*"?|`[^`]*`?|'(?:\\[^\r\n]|[^'\\\r\n])*'?|[\p{L}_][\p{L}\p{N}_]*|[^\s]/gu)]
        .filter(match => !match[0].startsWith('//') && !match[0].startsWith('/*'))
        .map(match => ({ text: match[0], start: match.index, end: match.index + match[0].length }));
}

function findBuilderCall(tokens: Token[]): number {
    return tokens.findIndex((token, index) =>
        /^[\p{L}_][\p{L}\p{N}_]*$/u.test(token.text)
        && tokens[index + 1]?.text === '.'
        && tokens[index + 2]?.text === 'CreateBuilder'
        && tokens[index + 3]?.text === '(');
}

function findStatementStart(tokens: Token[], index: number, text: string): number {
    let depth = [')', ']'].includes(tokens[index].text) ? 1 : 0;
    while (index > 0) {
        const previous = tokens[index - 1];
        if (depth === 0) {
            if ([';', '{', '}'].includes(previous.text)) {
                break;
            }
            const gap = text.slice(previous.end, tokens[index].start);
            if (gap.includes('\n') && !['=', ',', '.', '('].includes(previous.text)) {
                break;
            }
        }
        if ([')', ']'].includes(previous.text)) {
            depth++;
        }
        else if (['(', '['].includes(previous.text)) {
            // An enclosing call can start before this resource's receiver, as in
            // append(resources, builder.AddRedis("cache")); it is not an unmatched close.
            depth = Math.max(0, depth - 1);
        }
        index--;
    }

    return tokens[index].start;
}

class GoAppHostParser implements AppHostResourceParser {
    getSupportedExtensions(): string[] {
        return ['.go'];
    }

    async isAppHostFile(document: vscode.TextDocument): Promise<boolean> {
        return findBuilderCall(tokenize(document.getText())) >= 0;
    }

    async parseResources(document: vscode.TextDocument): Promise<ParsedResource[]> {
        const text = document.getText();
        const tokens = tokenize(text);
        const resources: ParsedResource[] = [];
        for (let index = 1; index < tokens.length - 4; index++) {
            const dot = tokens[index];
            const method = tokens[index + 1];
            const argument = tokens[index + 3];
            if (dot.text !== '.' || !/^Add[A-Z][A-Za-z0-9]*$/.test(method.text)
                || tokens[index + 2].text !== '('
                || ![',', ')'].includes(tokens[index + 4].text)) {
                continue;
            }

            const name = decodeString(argument.text);
            if (name === undefined) {
                continue;
            }

            resources.push({
                name,
                methodName: method.text,
                kind: method.text === 'AddStep' ? 'pipelineStep' : 'resource',
                range: new vscode.Range(document.positionAt(dot.start), document.positionAt(argument.end)),
                statementStartLine: document.positionAt(findStatementStart(tokens, index - 1, text)).line,
            });
        }

        return resources;
    }

    async findAppHostEntryPointLine(document: vscode.TextDocument): Promise<number | undefined> {
        const tokens = tokenize(document.getText());
        const main = tokens.findIndex((token, index) => token.text === 'func'
            && tokens[index + 1]?.text === 'main'
            && tokens[index + 2]?.text === '('
            && tokens[index + 3]?.text === ')');
        return main >= 0 ? document.positionAt(tokens[main].start).line : undefined;
    }

    async findBuilderStatementLine(document: vscode.TextDocument): Promise<number | undefined> {
        const tokens = tokenize(document.getText());
        const builder = findBuilderCall(tokens);
        if (builder < 0) {
            return undefined;
        }

        return document.positionAt(findStatementStart(tokens, builder, document.getText())).line;
    }

    async filterActiveOffsets(document: vscode.TextDocument, offsets: readonly number[]): Promise<number[]> {
        return filterActiveOffsetsInPlainText('go', document.getText(), offsets);
    }
}

registerParser(new GoAppHostParser());

function decodeString(text: string): string | undefined {
    if (text.startsWith('`') && text.endsWith('`') && text.length >= 2) {
        return text.slice(1, -1).replaceAll('\r', '');
    }
    if (!text.startsWith('"') || !text.endsWith('"') || text.length < 2) {
        return undefined;
    }

    const body = text.slice(1, -1);
    const bytes: number[] = [];
    for (let index = 0; index < body.length; index++) {
        if (body[index] !== '\\') {
            const codePoint = body.codePointAt(index)!;
            bytes.push(...Buffer.from(String.fromCodePoint(codePoint)));
            if (codePoint > 0xffff) {
                index++;
            }
            continue;
        }
        const escape = body[++index];
        const simple: Record<string, string> = {
            a: '\x07', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t', v: '\v', '\\': '\\', '"': '"',
        };
        if (simple[escape] !== undefined) {
            bytes.push(...Buffer.from(simple[escape]));
            continue;
        }

        const length = escape === 'x' ? 2 : escape === 'u' ? 4 : escape === 'U' ? 8 : 3;
        const octal = /^[0-7]$/.test(escape);
        const digits = body.slice(octal ? index : index + 1, (octal ? index : index + 1) + length);
        if (digits.length !== length || !(octal ? /^[0-7]+$/ : /^[0-9a-fA-F]+$/).test(digits)
            || (!octal && !['x', 'u', 'U'].includes(escape))) {
            return undefined;
        }

        const codePoint = parseInt(digits, octal ? 8 : 16);
        if (codePoint > (octal ? 255 : 0x10ffff) || (codePoint >= 0xd800 && codePoint <= 0xdfff)) {
            return undefined;
        }
        if (octal || escape === 'x') {
            bytes.push(codePoint);
        }
        else {
            bytes.push(...Buffer.from(String.fromCodePoint(codePoint)));
        }
        index += length - (octal ? 1 : 0);
    }

    // Byte escapes such as "\xc3\xa9" encode one UTF-8 character, not two Unicode scalars.
    // Invalid UTF-8 cannot identify a resource returned by the CLI's JSON protocol.
    try {
        return new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(new Uint8Array(bytes));
    }
    catch (error) {
        if (error instanceof TypeError) {
            return undefined;
        }
        throw error;
    }
}
