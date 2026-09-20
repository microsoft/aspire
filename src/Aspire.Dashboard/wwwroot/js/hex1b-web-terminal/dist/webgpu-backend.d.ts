import type { RenderBackend, RenderBatch, RenderColor, RenderTexture } from "./render-backend.js";
export declare class WebGpuBackend implements RenderBackend {
    readonly device: GPUDevice;
    readonly kind = "webgpu";
    instanceBufferBytes: number;
    instanceBuffer: GPUBuffer | undefined;
    disposed: boolean;
    context?: GPUCanvasContext;
    format: GPUTextureFormat;
    uniform: GPUBuffer;
    sampler: GPUSampler;
    pipeline?: GPURenderPipeline;
    static create(canvas: OffscreenCanvas, onFatal: (error: Error) => void): Promise<WebGpuBackend>;
    constructor(device: GPUDevice);
    get maxTextureDimension2D(): number;
    get maxCanvasDimension2D(): number;
    initialize(canvas: OffscreenCanvas): Promise<void>;
    createTexture(width: number, height: number, label: string): RenderTexture;
    resize(width: number, height: number): void;
    submit(instances: Float32Array<ArrayBuffer>, quadCount: number, batches: readonly RenderBatch[], background: RenderColor): void;
    idle(): Promise<void>;
    dispose(): void;
}
//# sourceMappingURL=webgpu-backend.d.ts.map