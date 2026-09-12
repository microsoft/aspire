export declare const QUAD_STRIDE = 16;
export type RenderColor = [number, number, number, number];
export type RenderPixels = Uint8Array<ArrayBuffer> | Uint8ClampedArray<ArrayBuffer>;
export interface RenderTexture {
    readonly width: number;
    readonly height: number;
    writePixels(pixels: RenderPixels, width: number, height: number, x?: number, y?: number): void;
    writeBitmap(bitmap: ImageBitmap): void;
    destroy(): void;
}
export interface RenderBatch {
    resource: RenderTexture;
    start: number;
    count: number;
}
/** Internal graphics boundary; layout, rasterization and ordering belong to TerminalRenderer. */
export interface RenderBackend {
    readonly kind: "webgpu" | "webgl2";
    readonly maxTextureDimension2D: number;
    readonly maxCanvasDimension2D: number;
    readonly instanceBufferBytes: number;
    createTexture(width: number, height: number, label: string): RenderTexture;
    resize(width: number, height: number): void;
    submit(instances: Float32Array<ArrayBuffer>, quadCount: number, batches: readonly RenderBatch[], background: RenderColor): void;
    idle(): Promise<void>;
    dispose(): void;
}
/** Only capability/device acquisition failures may trigger automatic backend fallback. */
export declare class RendererUnavailableError extends Error {
}
//# sourceMappingURL=render-backend.d.ts.map