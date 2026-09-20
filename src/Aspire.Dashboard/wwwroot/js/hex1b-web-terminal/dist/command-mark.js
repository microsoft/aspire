/**
 * Parses a {@link TerminalCommandMark.rawParameters} string into key/value pairs, mirroring the
 * server's `TerminalCommandMark.Parameters`: segments are split on `;`, each split on the first
 * `=`; segments without `=` are ignored. Values are not decoded (e.g. percent-decoding is left
 * to the caller for keys that use it, such as `cmdline_url`). Returns an empty map for null/"".
 */
export function parseCommandMarkParameters(rawParameters) {
    const result = new Map();
    if (!rawParameters)
        return result;
    for (const segment of rawParameters.split(";")) {
        const separator = segment.indexOf("=");
        if (separator < 0)
            continue;
        result.set(segment.slice(0, separator), segment.slice(separator + 1));
    }
    return result;
}
/**
 * Gets the raw (still percent-encoded) `cmdline_url` parameter from a command mark, or null when
 * absent. This is a Contour-originated, non-universal extension to OSC 133;C.
 */
export function getCmdlineUrl(mark) {
    return mark ? parseCommandMarkParameters(mark.rawParameters).get("cmdline_url") ?? null : null;
}
//# sourceMappingURL=command-mark.js.map