import type { FontMetrics, LoadedFont, NormalizedFont } from "./terminal-font.js";
import type { TerminalFont, TerminalSize } from "./types.js";
import type { FrameImage, FrameMetadata, ImagePlacement, TerminalCell } from "./wire-types.js";
type Vector4 = [number, number, number, number];
interface TextureResource {
    texture: GPUTexture;
    bindGroup: GPUBindGroup;
    width: number;
    height: number;
}
interface Shelf {
    x: number;
    y: number;
    rowHeight: number;
}
interface GlyphPlacement {
    key: string;
    cell: TerminalCell;
    x: number;
    y: number;
    width: number;
    height: number;
}
interface Glyph {
    colored: boolean;
    u0: number;
    v0: number;
    u1: number;
    v1: number;
}
interface Batch {
    resource: TextureResource;
    start: number;
    count: number;
}
/** WebGPU instanced quads; Canvas2D is used only to rasterize reusable glyphs. */
export declare class TerminalRenderer {
    canvas: OffscreenCanvas;
    scale: number;
    backingScale: number;
    device: GPUDevice;
    fontConfiguration: NormalizedFont;
    fontMetrics: Map<number, FontMetrics>;
    context: GPUCanvasContext;
    images: Map<string, TextureResource>;
    glyphs: Map<string, Glyph>;
    imageUploadBytes: number;
    imagePayloadBytes: number;
    glyphUploadBytes: number;
    atlasRebuilds: number;
    textureBytes: number;
    instances: Float32Array<ArrayBuffer>;
    instanceBuffer: GPUBuffer | null;
    instanceBufferBytes: number;
    disposed: boolean;
    columns: number;
    rows: number;
    font: LoadedFont;
    format: GPUTextureFormat;
    uniform: GPUBuffer;
    sampler: GPUSampler;
    pipeline: GPURenderPipeline;
    rasterCanvas: OffscreenCanvas;
    raster: OffscreenCanvasRenderingContext2D;
    atlas: TextureResource;
    glyphKeyUnits: number;
    shelf: Shelf;
    canvasLimited: boolean;
    width: number;
    height: number;
    quadCount: number;
    batches: Batch[];
    static create(canvas: OffscreenCanvas, scale: number, onFatal: (error: Error | GPUError) => void, font: TerminalFont): Promise<TerminalRenderer>;
    constructor(canvas: OffscreenCanvas, scale: number, device: GPUDevice, font: NormalizedFont);
    initialize(): Promise<void>;
    createTexture(width: number, height: number, label: string): TextureResource;
    resetAtlas(size: number): void;
    resize(columns: number, rows: number, viewport?: TerminalSize): void;
    /** Call only between submissions. Missing/over-budget resources terminate the session. */
    updateImages(incoming: readonly FrameImage[], retainedKeys: readonly string[]): Promise<void>;
    prepareGlyphs(cells: readonly (TerminalCell | undefined)[]): void;
    uploadGlyph({ key, cell, x, y, width, height }: GlyphPlacement): void;
    /** Clip geometry and UVs together. Only adjacent compatible textures may coalesce. */
    quad(resource: TextureResource, x: number, y: number, width: number, height: number, color: Vector4, mode?: number, uv?: Vector4, clip?: Vector4): void;
    solid(x: number, y: number, width: number, height: number, color: Vector4): void;
    placement(placement: ImagePlacement): void;
    decorations(cell: TerminalCell, x: number, y: number, width: number, foreground: Vector4): void;
    render(cells: readonly (TerminalCell | undefined)[], metadata: FrameMetadata, blinkOn: boolean): {
        cpuMs: number;
        quads: number;
        drawCalls: number;
    };
    metrics(): {
        fontFamily: string;
        rasterScale: number;
        backingScale: number;
        backingWidth: number;
        backingHeight: number;
        imageCount: number;
        textureBytes: number;
        atlasGlyphs: number;
        atlasBytes: number;
        atlasRebuilds: number;
        imageUploadBytes: number;
        imagePayloadBytes: number;
        glyphUploadBytes: number;
        instanceBufferBytes: number;
    };
    idle(): Promise<void>;
    dispose(): void;
}
export {};
//# sourceMappingURL=renderer.d.ts.map