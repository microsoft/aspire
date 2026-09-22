import type { TerminalCommandMark } from "./types.js";
/**
 * Parses a {@link TerminalCommandMark.rawParameters} string into key/value pairs, mirroring the
 * server's `TerminalCommandMark.Parameters`: segments are split on `;`, each split on the first
 * `=`; segments without `=` are ignored. Values are not decoded (e.g. percent-decoding is left
 * to the caller for keys that use it, such as `cmdline_url`). Returns an empty map for null/"".
 */
export declare function parseCommandMarkParameters(rawParameters: string | null): ReadonlyMap<string, string>;
/**
 * Gets the raw (still percent-encoded) `cmdline_url` parameter from a command mark, or null when
 * absent. This is a Contour-originated, non-universal extension to OSC 133;C.
 */
export declare function getCmdlineUrl(mark: TerminalCommandMark | null): string | null;
//# sourceMappingURL=command-mark.d.ts.map