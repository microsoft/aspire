import type { SelectionRange } from "./types.js";
import type { TerminalLinkTextChunk, TerminalLinkTextMode } from "./link-types.js";
import type { LinkDetectionSnapshot } from "./link-detection.js";
interface Span extends SelectionRange {
    start: number;
    end: number;
}
export interface MappedLinkChunk {
    chunk: TerminalLinkTextChunk;
    spans: Span[];
    boundaries: Set<number>;
    unsafe: Set<number>;
    key: string;
}
export declare function extractLinkText(snapshot: LinkDetectionSnapshot, mode: TerminalLinkTextMode): MappedLinkChunk[];
export declare function mapLinkRange(mapped: MappedLinkChunk, index: number, length: number): SelectionRange[] | null;
export {};
//# sourceMappingURL=link-text.d.ts.map