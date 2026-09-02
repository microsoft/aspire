/**
 * Escapes the Markdown constructs that change how an identity renders inline.
 *
 * Confirmation bodies render as Markdown, so an unescaped `*`, `_`, `` ` ``, `[`, or
 * `<` in a resolved resource or AppHost identity would show the user something other than
 * the identity the tool will act on. Escaping keeps rendered text one-to-one with that
 * identity instead of deleting characters, which would break that relationship in the
 * other direction. Characters meaningful only at the start of a line (`.`, `-`, `{`, `}`)
 * are left alone because callers interpolate values mid-sentence and those characters are
 * common in real resource and project names.
 * See https://spec.commonmark.org/0.31.2/#backslash-escapes
 */
export function escapeMarkdownForConfirmation(value: string): string {
    return value.replace(/[\\`*_[\]()<>#+~|!&]/g, character => `\\${character}`);
}
