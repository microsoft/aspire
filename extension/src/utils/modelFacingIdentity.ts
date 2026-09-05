/**
 * Characters that change what an identity is without changing, or while changing, how it
 * looks: C0/C1 controls and DEL, plus every Unicode format character (`\p{Cf}`).
 *
 * Bidi controls (U+202A-U+202E, U+2066-U+2069) reorder the run that follows them, so an
 * identity can render as a completely different one. Zero-width characters (U+200B-U+200D)
 * are invisible, so two distinct identities can render identically. Reject the identity
 * rather than deleting characters, because deletion would break its one-to-one relationship
 * with the target it names.
 *
 * See https://unicode.org/reports/tr9/ and
 * https://unicode.org/reports/tr36/#Bidirectional_Text_Spoofing
 */
const identityChangingCharacters = /[\u0000-\u001F\u007F-\u009F]|\p{Cf}/u;

export function isSafeModelFacingIdentity(value: string, maximumLength: number): boolean {
    return value.length <= maximumLength && !identityChangingCharacters.test(value);
}
