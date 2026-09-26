import type { TerminalPoint } from "./types.js";
import type { HyperlinkRange } from "./wire-types.js";
/** OSC 8 destinations are untrusted output, not page-relative navigation. */
export declare function hyperlinkUri(uri: string): string | null;
export declare class Hyperlinks {
    #private;
    update(ranges: readonly HyperlinkRange[]): void;
    at(point: TerminalPoint): string | null;
}
//# sourceMappingURL=hyperlinks.d.ts.map