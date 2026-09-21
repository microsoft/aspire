import type { RenderBackend, RenderBatch, RenderColor, RenderTexture } from "./render-backend.js";
export declare class WebGl2Backend implements RenderBackend {
    private readonly canvas;
    readonly gl: WebGL2RenderingContext;
    private readonly onFatal;
    readonly kind = "webgl2";
    private textureLimit;
    private canvasLimit;
    private bufferBytes;
    private disposed;
    private lostError?;
    private program?;
    private instanceBuffer?;
    private vertexArray?;
    private viewport?;
    private readonly shaders;
    private readonly textures;
    private readonly fences;
    private readonly onContextLost;
    static create(canvas: OffscreenCanvas, onFatal: (error: Error) => void): Promise<WebGl2Backend>;
    private constructor();
    get maxTextureDimension2D(): number;
    get maxCanvasDimension2D(): number;
    get instanceBufferBytes(): number;
    private initialize;
    private compile;
    private assertActive;
    private contextLost;
    private checkErrors;
    createTexture(width: number, height: number, label: string): RenderTexture;
    resize(width: number, height: number): void;
    submit(instances: Float32Array<ArrayBuffer>, quadCount: number, batches: readonly RenderBatch[], background: RenderColor): void;
    idle(): Promise<void>;
    private pollFence;
    private settleFence;
    dispose(): void;
}
//# sourceMappingURL=webgl2-backend.d.ts.map