export declare const MIN_FONT_SIZE = 8;
export declare const MAX_FONT_SIZE = 32;
export declare function dimensions(columns: number, rows: number): TerminalGrid;
export declare function normalizeSizing(sizing?: TerminalSizing, previousFontSize?: number): TerminalSizingState;
export declare function requestedGrid(size: TerminalSize, geometry: TerminalGeometry, sizing: TerminalSizingState): TerminalGrid | null;
export declare function fittedScale(size: TerminalSize, geometry: TerminalGeometry, isPrimary: boolean, sizing: TerminalSizingState): number;
import type { TerminalGeometry, TerminalGrid, TerminalSize, TerminalSizing, TerminalSizingState } from "./types.js";
//# sourceMappingURL=terminal-sizing.d.ts.map