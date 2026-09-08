import { QUAD_STRIDE, RendererUnavailableError } from "./render-backend.js";
const shader = /* wgsl */ `
struct Viewport { size: vec2f, padding: vec2f }
@group(0) @binding(0) var<uniform> viewport: Viewport;
@group(0) @binding(1) var image: texture_2d<f32>;
@group(0) @binding(2) var imageSampler: sampler;

struct VertexOut {
  @builtin(position) position: vec4f,
  @location(0) uv: vec2f,
  @location(1) color: vec4f,
  @location(2) @interpolate(flat) mode: f32,
}

@vertex fn vertex(
  @builtin(vertex_index) index: u32,
  @location(0) rect: vec4f,
  @location(1) uvRect: vec4f,
  @location(2) color: vec4f,
  @location(3) mode: f32,
) -> VertexOut {
  let corners = array<vec2f, 6>(
    vec2f(0, 0), vec2f(1, 0), vec2f(0, 1),
    vec2f(0, 1), vec2f(1, 0), vec2f(1, 1)
  );
  let corner = corners[index];
  let position = rect.xy + corner * rect.zw;
  var out: VertexOut;
  out.position = vec4f(position / viewport.size * vec2f(2, -2) + vec2f(-1, 1), 0, 1);
  out.uv = mix(uvRect.xy, uvRect.zw, corner);
  out.color = color;
  out.mode = mode;
  return out;
}

@fragment fn fragment(in: VertexOut) -> @location(0) vec4f {
  // Explicit LOD avoids derivative-uniformity requirements across solid/mask/image batches.
  let texel = textureSampleLevel(image, imageSampler, in.uv, 0);
  if (in.mode < 0.5) { return in.color; }
  if (in.mode < 1.5) { return vec4f(in.color.rgb, in.color.a * texel.a); }
  return texel * in.color;
}`;
class WebGpuTexture {
    device;
    texture;
    bindGroup;
    width;
    height;
    constructor(device, texture, bindGroup, width, height) {
        this.device = device;
        this.texture = texture;
        this.bindGroup = bindGroup;
        this.width = width;
        this.height = height;
    }
    writePixels(pixels, width, height, x = 0, y = 0) {
        this.device.queue.writeTexture({ texture: this.texture, origin: [x, y] }, pixels, { bytesPerRow: width * 4, rowsPerImage: height }, [width, height]);
    }
    writeBitmap(bitmap) {
        this.device.queue.copyExternalImageToTexture({ source: bitmap }, { texture: this.texture, premultipliedAlpha: false }, [this.width, this.height]);
    }
    destroy() { this.texture.destroy(); }
}
export class WebGpuBackend {
    device;
    kind = "webgpu";
    instanceBufferBytes = 0;
    instanceBuffer;
    disposed = false;
    context;
    format;
    uniform;
    sampler;
    pipeline;
    static async create(canvas, onFatal) {
        if (!globalThis.isSecureContext)
            throw new RendererUnavailableError("WebGPU requires HTTPS or localhost");
        if (!navigator.gpu)
            throw new RendererUnavailableError("WebGPU is unavailable in this browser worker");
        const adapter = await navigator.gpu.requestAdapter();
        if (!adapter)
            throw new RendererUnavailableError("No WebGPU adapter is available");
        let device;
        try {
            device = await adapter.requestDevice();
        }
        catch (error) {
            if (error instanceof DOMException && error.name === "OperationError") {
                throw new RendererUnavailableError(`Could not acquire a WebGPU device: ${error.message}`, { cause: error });
            }
            throw error;
        }
        let backend;
        try {
            backend = new WebGpuBackend(device);
            const active = backend;
            device.lost.then(info => {
                if (!active.disposed)
                    onFatal(new Error(`WebGPU device lost: ${info.message || info.reason}`));
            });
            device.addEventListener("uncapturederror", event => {
                if (!active.disposed)
                    onFatal(new Error(`WebGPU error: ${event.error.message}`));
            });
            await backend.initialize(canvas);
            return backend;
        }
        catch (error) {
            if (backend)
                backend.dispose();
            else
                device.destroy();
            throw error;
        }
    }
    constructor(device) {
        this.device = device;
        this.format = navigator.gpu.getPreferredCanvasFormat();
        this.uniform = device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        this.sampler = device.createSampler({ minFilter: "linear", magFilter: "linear" });
    }
    get maxTextureDimension2D() { return this.device.limits.maxTextureDimension2D; }
    get maxCanvasDimension2D() { return this.maxTextureDimension2D; }
    async initialize(canvas) {
        const module = this.device.createShaderModule({ code: shader });
        this.pipeline = await this.device.createRenderPipelineAsync({
            layout: "auto",
            vertex: {
                module,
                entryPoint: "vertex",
                buffers: [{
                        arrayStride: QUAD_STRIDE * 4,
                        stepMode: "instance",
                        attributes: [
                            { shaderLocation: 0, offset: 0, format: "float32x4" },
                            { shaderLocation: 1, offset: 16, format: "float32x4" },
                            { shaderLocation: 2, offset: 32, format: "float32x4" },
                            { shaderLocation: 3, offset: 48, format: "float32" },
                        ],
                    }],
            },
            fragment: {
                module,
                entryPoint: "fragment",
                targets: [{
                        format: this.format,
                        blend: {
                            color: { srcFactor: "src-alpha", dstFactor: "one-minus-src-alpha" },
                            alpha: { srcFactor: "one", dstFactor: "one-minus-src-alpha" },
                        },
                    }],
            },
            primitive: { topology: "triangle-list" },
        });
        // Acquire the presentation surface last. A null context leaves it usable by WebGL2.
        const context = canvas.getContext("webgpu");
        if (!context)
            throw new RendererUnavailableError("Could not create an OffscreenCanvas WebGPU context");
        this.context = context;
        context.configure({ device: this.device, format: this.format, alphaMode: "opaque" });
    }
    createTexture(width, height, label) {
        if (width > this.maxTextureDimension2D || height > this.maxTextureDimension2D) {
            throw new Error(`${label} exceeds the GPU texture dimension limit`);
        }
        if (!this.pipeline)
            throw new Error("WebGPU backend is not initialized");
        const texture = this.device.createTexture({
            label, size: [width, height], format: "rgba8unorm",
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
        });
        try {
            const bindGroup = this.device.createBindGroup({
                layout: this.pipeline.getBindGroupLayout(0),
                entries: [
                    { binding: 0, resource: { buffer: this.uniform } },
                    { binding: 1, resource: texture.createView() },
                    { binding: 2, resource: this.sampler },
                ],
            });
            return new WebGpuTexture(this.device, texture, bindGroup, width, height);
        }
        catch (error) {
            texture.destroy();
            throw error;
        }
    }
    resize(width, height) {
        this.device.queue.writeBuffer(this.uniform, 0, new Float32Array([width, height, 0, 0]));
    }
    submit(instances, quadCount, batches, background) {
        if (this.disposed || !this.context || !this.pipeline)
            throw new Error("WebGPU backend is not initialized");
        const usedBytes = quadCount * QUAD_STRIDE * 4;
        if (!this.instanceBuffer || usedBytes > this.instanceBufferBytes) {
            this.instanceBuffer?.destroy();
            this.instanceBufferBytes = Math.max(256, instances.byteLength);
            this.instanceBuffer = this.device.createBuffer({
                size: this.instanceBufferBytes, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST,
            });
        }
        if (usedBytes)
            this.device.queue.writeBuffer(this.instanceBuffer, 0, instances, 0, quadCount * QUAD_STRIDE);
        const encoder = this.device.createCommandEncoder();
        const pass = encoder.beginRenderPass({
            colorAttachments: [{
                    view: this.context.getCurrentTexture().createView(),
                    clearValue: { r: background[0], g: background[1], b: background[2], a: 1 },
                    loadOp: "clear", storeOp: "store",
                }],
        });
        pass.setPipeline(this.pipeline);
        pass.setVertexBuffer(0, this.instanceBuffer);
        for (const batch of batches) {
            if (!(batch.resource instanceof WebGpuTexture))
                throw new Error("Texture belongs to a different rendering backend");
            pass.setBindGroup(0, batch.resource.bindGroup);
            pass.draw(6, batch.count, 0, batch.start);
        }
        pass.end();
        this.device.queue.submit([encoder.finish()]);
    }
    async idle() { await this.device.queue.onSubmittedWorkDone(); }
    dispose() {
        if (this.disposed)
            return;
        this.disposed = true;
        this.instanceBuffer?.destroy();
        this.uniform.destroy();
        this.context?.unconfigure();
        this.device.destroy();
    }
}
//# sourceMappingURL=webgpu-backend.js.map