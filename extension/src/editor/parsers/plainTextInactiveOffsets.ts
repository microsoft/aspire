/**
 * Offset classification for AppHost languages that have no resource parser.
 *
 * The tree-sitter and TypeScript parsers answer "is this offset inside a comment or a string?" from a
 * real syntax tree. Java, Python and Go AppHosts have no parser — they are recognised only well enough
 * to surface the Spring Boot Dashboard warning — so this scanner answers the same question from the
 * source text. It is not a lexer for those languages: it tracks only the three states a warning needs
 * to distinguish (code, comment, string literal), which is exactly enough to avoid warning about a
 * commented-out or quoted `withMavenGoal("spring-boot:run")`.
 */

interface StringDelimiter {
    /** The opening (and, for every language handled here, closing) delimiter. */
    readonly delimiter: string;
    /** Whether a backslash escapes the following character, as in Java's `"` but not Go's backtick. */
    readonly supportsEscapes: boolean;
    /** Whether the literal may span lines, as in a Java text block but not a Java string. */
    readonly multiline: boolean;
}

interface PlainTextSyntax {
    readonly lineComment: string;
    readonly blockCommentOpen?: string;
    readonly blockCommentClose?: string;
    /**
     * Ordered longest-first so that a Java text block or a Python triple-quote is matched before the
     * single-character delimiter it starts with.
     */
    readonly strings: readonly StringDelimiter[];
}

const cFamilyStrings: readonly StringDelimiter[] = [
    { delimiter: '"""', supportsEscapes: true, multiline: true },
    { delimiter: '"', supportsEscapes: true, multiline: false },
    { delimiter: "'", supportsEscapes: true, multiline: false },
];

const syntaxByLanguageId: ReadonlyMap<string, PlainTextSyntax> = new Map([
    // Text blocks (`"""`) are Java 15+ and can legally contain `//`, so they are matched first.
    ['java', { lineComment: '//', blockCommentOpen: '/*', blockCommentClose: '*/', strings: cFamilyStrings }],
    [
        'go',
        {
            lineComment: '//',
            blockCommentOpen: '/*',
            blockCommentClose: '*/',
            // A raw string literal spans lines and gives backslash no special meaning.
            // https://go.dev/ref/spec#String_literals
            strings: [
                { delimiter: '`', supportsEscapes: false, multiline: true },
                { delimiter: '"', supportsEscapes: true, multiline: false },
                { delimiter: "'", supportsEscapes: true, multiline: false },
            ],
        },
    ],
    [
        'python',
        {
            lineComment: '#',
            strings: [
                { delimiter: '"""', supportsEscapes: true, multiline: true },
                { delimiter: "'''", supportsEscapes: true, multiline: true },
                { delimiter: '"', supportsEscapes: true, multiline: false },
                { delimiter: "'", supportsEscapes: true, multiline: false },
            ],
        },
    ],
]);

/** The AppHost languages this scanner can classify offsets for. */
export function getPlainTextScannableLanguageIds(): string[] {
    return [...syntaxByLanguageId.keys()];
}

/**
 * Returns the subset of <paramref name="offsets"/> that fall in code rather than in a comment or a
 * string literal, or all of them when the language is not one this scanner knows.
 *
 * A single forward scan classifies the whole document, which is cheaper than restarting at each offset
 * and is what makes the scanner correct for multi-line constructs: whether a given `//` opens a comment
 * depends entirely on the state left behind by everything before it.
 */
export function filterActiveOffsetsInPlainText(languageId: string, text: string, offsets: readonly number[]): number[] {
    const syntax = syntaxByLanguageId.get(languageId);
    if (!syntax) {
        return [...offsets];
    }

    const inactive = classifyInactiveRegions(syntax, text);

    return offsets.filter(offset => !inactive(offset));
}

/**
 * Scans once and returns a predicate over offsets. Regions are collected as half-open intervals in
 * ascending order, so the lookup is a binary search rather than a rescan.
 */
function classifyInactiveRegions(syntax: PlainTextSyntax, text: string): (offset: number) => boolean {
    const regions: Array<{ start: number; end: number }> = [];
    let index = 0;

    while (index < text.length) {
        if (text.startsWith(syntax.lineComment, index)) {
            const newline = text.indexOf('\n', index);
            const end = newline < 0 ? text.length : newline;
            regions.push({ start: index, end });
            index = end;
            continue;
        }

        if (syntax.blockCommentOpen && syntax.blockCommentClose && text.startsWith(syntax.blockCommentOpen, index)) {
            const close = text.indexOf(syntax.blockCommentClose, index + syntax.blockCommentOpen.length);
            // An unterminated block comment runs to the end of the file, which is also how a compiler
            // sees it — the rest of the document really is commented out.
            const end = close < 0 ? text.length : close + syntax.blockCommentClose.length;
            regions.push({ start: index, end });
            index = end;
            continue;
        }

        const opened = syntax.strings.find(candidate => text.startsWith(candidate.delimiter, index));
        if (opened) {
            const end = findStringEnd(text, index, opened);
            regions.push({ start: index, end });
            index = end;
            continue;
        }

        index++;
    }

    return offset => {
        let low = 0;
        let high = regions.length - 1;
        while (low <= high) {
            const middle = (low + high) >> 1;
            const region = regions[middle];
            if (offset < region.start) {
                high = middle - 1;
            } else if (offset >= region.end) {
                low = middle + 1;
            } else {
                return true;
            }
        }

        return false;
    };
}

/** Returns the offset just past the literal's closing delimiter, or the point at which it is abandoned. */
function findStringEnd(text: string, start: number, delimiter: StringDelimiter): number {
    let index = start + delimiter.delimiter.length;

    while (index < text.length) {
        const character = text[index];

        if (delimiter.supportsEscapes && character === '\\') {
            index += 2;
            continue;
        }

        // A single-quoted literal that reaches a newline was never a literal — most often an apostrophe
        // in a comment-like sentence. Ending it at the newline stops one stray quote from swallowing the
        // remainder of the file and suppressing every later warning.
        if (!delimiter.multiline && character === '\n') {
            return index;
        }

        if (text.startsWith(delimiter.delimiter, index)) {
            return index + delimiter.delimiter.length;
        }

        index++;
    }

    return text.length;
}
