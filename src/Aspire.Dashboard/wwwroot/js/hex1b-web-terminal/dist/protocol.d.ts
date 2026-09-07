import type { HistoryMetadata, TerminalCell, TerminalFrame } from "./wire-types.js";
export declare const LIMITS: Readonly<{
    commandBytes: number;
    frameBytes: number;
    metadataBytes: number;
    cells: 262144;
    images: 4096;
    placements: 16384;
    textureBytes: number;
}>;
export declare function assertCommandSize(command: unknown): void;
export declare function validateHistory(history: unknown, columns: number, rows: number): asserts history is HistoryMetadata | null;
/** Decode one complete, experimental HWT1 message, checking every boundary before reading. */
export declare function decodeFrame(buffer: unknown): TerminalFrame;
/** Mirror text, not ANSI; continuation cells are already represented by their lead glyph. */
export declare function screenText(cells: readonly (TerminalCell | undefined)[], columns: number, rows: number): string;
//# sourceMappingURL=protocol.d.ts.map