/**
 * Escapes the Markdown constructs that change how a path renders inline.
 *
 * Tool confirmation bodies render as Markdown, so an unescaped `*`, `_`, `` ` ``, `[`,
 * or `<` in a real file name would show the user something other than the file the tool
 * is about to act on. Characters meaningful only at the start of a line are omitted
 * because paths are always interpolated mid-sentence.
 * See https://spec.commonmark.org/0.31.2/#backslash-escapes
 */
export function escapeMarkdown(value: string): string {
    return value.replace(/[\\`*_[\]()<>#+~|!&]/g, character => `\\${character}`);
}
