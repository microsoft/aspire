/** Resolve caller-relative URLs before sending the configuration to the worker. */
export declare function normalizeFont(font?: TerminalFont, baseUrl?: string): NormalizedFont;
/** Use the rendering context's FontFaceSet; a worker cannot inherit page fonts. */
export declare function loadFont(configuration: TerminalFont): Promise<LoadedFont>;
/** Fit one font-wide advance and line box to the authoritative cell dimensions. */
export declare function measureFont(raster: OffscreenCanvasRenderingContext2D, cssFamily: string, attributes: number, scale: number, cellWidth: number, cellHeight: number): FontMetrics;
import type { TerminalFont, TerminalFontFace } from "./types.js";
export interface NormalizedFont {
    family: string;
    faces: Required<TerminalFontFace>[];
}
export interface LoadedFont {
    family: string;
    cssFamily: string;
    dispose(): void;
}
export interface FontMetrics {
    font: string;
    xScale: number;
    yScale: number;
    baseline: number;
}
//# sourceMappingURL=terminal-font.d.ts.map