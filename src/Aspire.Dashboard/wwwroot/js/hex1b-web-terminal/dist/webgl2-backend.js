import { QUAD_STRIDE, RendererUnavailableError } from "./render-backend.js";
const vertexShader = /* glsl */ `#version 300 es
precision highp float;
precision highp int;

layout(location = 0) in vec4 rect;
layout(location = 1) in vec4 uvRect;
layout(location = 2) in vec4 color;
layout(location = 3) in float mode;
uniform vec2 viewport;
out vec2 fragmentUv;
out vec4 fragmentTint;
flat out float fragmentMode;

void main() {
  const vec2 corners[6] = vec2[6](
    vec2(0, 0), vec2(1, 0), vec2(0, 1),
    vec2(0, 1), vec2(1, 0), vec2(1, 1)
  );
  vec2 corner = corners[gl_VertexID];
  vec2 position = rect.xy + corner * rect.zw;
  gl_Position = vec4(position / viewport * vec2(2, -2) + vec2(-1, 1), 0, 1);
  fragmentUv = mix(uvRect.xy, uvRect.zw, corner);
  fragmentTint = color;
  fragmentMode = mode;
}`;
const fragmentShader = /* glsl */ `#version 300 es
precision highp float;
precision highp int;

uniform highp sampler2D image;
in vec2 fragmentUv;
in vec4 fragmentTint;
flat in float fragmentMode;
out vec4 fragmentColor;

void main() {
  vec4 texel = textureLod(image, fragmentUv, 0.0);
  if (fragmentMode < 0.5) {
    fragmentColor = fragmentTint;
  } else if (fragmentMode < 1.5) {
    fragmentColor = vec4(fragmentTint.rgb, fragmentTint.a * texel.a);
  } else {
    fragmentColor = texel * fragmentTint;
  }
}`;
function errorFrom(value) {
    return value instanceof Error ? value : new Error(String(value));
}
function positiveInteger(value) {
    return Number.isSafeInteger(value) && value > 0;
}
function prepareUpload(gl) {
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
    gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE);
    gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
    gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, 0);
    gl.pixelStorei(gl.UNPACK_SKIP_ROWS, 0);
}
class WebGl2Texture {
    owner;
    gl;
    texture;
    width;
    height;
    assertActive;
    release;
    destroyed = false;
    constructor(owner, gl, texture, width, height, assertActive, release) {
        this.owner = owner;
        this.gl = gl;
        this.texture = texture;
        this.width = width;
        this.height = height;
        this.assertActive = assertActive;
        this.release = release;
    }
    bind() {
        this.assertActive();
        if (this.destroyed)
            throw new Error("WebGL2 texture is destroyed");
        this.gl.bindTexture(this.gl.TEXTURE_2D, this.texture);
        prepareUpload(this.gl);
    }
    writePixels(pixels, width, height, x = 0, y = 0) {
        if (!positiveInteger(width) || !positiveInteger(height) ||
            !Number.isSafeInteger(x) || !Number.isSafeInteger(y) || x < 0 || y < 0 ||
            x + width > this.width || y + height > this.height || pixels.byteLength < width * height * 4) {
            throw new Error("Invalid WebGL2 texture upload dimensions or pixel data");
        }
        this.bind();
        this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, x, y, width, height, this.gl.RGBA, this.gl.UNSIGNED_BYTE, pixels);
    }
    writeBitmap(bitmap) {
        if (bitmap.width !== this.width || bitmap.height !== this.height) {
            throw new Error("WebGL2 bitmap dimensions do not match the texture");
        }
        this.bind();
        // ImageBitmap ignores unpack conversion flags; the shared decoder supplies straight-alpha,
        // unconverted, top-down bitmaps. Raw RGBA uploads use the explicit unpack state above.
        this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, 0, 0, this.gl.RGBA, this.gl.UNSIGNED_BYTE, bitmap);
    }
    destroy() {
        if (this.destroyed)
            return;
        this.destroyed = true;
        this.release(this);
        this.gl.deleteTexture(this.texture);
    }
}
export class WebGl2Backend {
    canvas;
    gl;
    onFatal;
    kind = "webgl2";
    textureLimit = 0;
    canvasLimit = 0;
    bufferBytes = 0;
    disposed = false;
    lostError;
    program;
    instanceBuffer;
    vertexArray;
    viewport;
    shaders = new Set();
    textures = new Set();
    fences = new Set();
    onContextLost = (event) => {
        const message = event.statusMessage;
        this.contextLost(new Error(`WebGL2 context lost${message ? `: ${message}` : ""}`));
    };
    static async create(canvas, onFatal) {
        const gl = canvas.getContext("webgl2", {
            alpha: false,
            antialias: false,
            depth: false,
            stencil: false,
            premultipliedAlpha: false,
            preserveDrawingBuffer: false,
        });
        if (!gl)
            throw new RendererUnavailableError("Could not create an OffscreenCanvas WebGL2 context");
        const backend = new WebGl2Backend(canvas, gl, onFatal);
        try {
            backend.initialize();
            return backend;
        }
        catch (error) {
            backend.dispose();
            throw error;
        }
    }
    constructor(canvas, gl, onFatal) {
        this.canvas = canvas;
        this.gl = gl;
        this.onFatal = onFatal;
    }
    get maxTextureDimension2D() { return this.textureLimit; }
    get maxCanvasDimension2D() { return this.canvasLimit; }
    get instanceBufferBytes() { return this.bufferBytes; }
    initialize() {
        const gl = this.gl;
        this.canvas.addEventListener("webglcontextlost", this.onContextLost);
        this.checkErrors("initialization");
        this.textureLimit = gl.getParameter(gl.MAX_TEXTURE_SIZE);
        const viewportLimit = gl.getParameter(gl.MAX_VIEWPORT_DIMS);
        this.canvasLimit = Math.min(this.textureLimit, gl.getParameter(gl.MAX_RENDERBUFFER_SIZE), viewportLimit[0], viewportLimit[1]);
        if (!positiveInteger(this.textureLimit) || !positiveInteger(this.canvasLimit)) {
            throw new Error("Invalid WebGL2 texture or viewport limits");
        }
        const vertex = this.compile(gl.VERTEX_SHADER, vertexShader);
        const fragment = this.compile(gl.FRAGMENT_SHADER, fragmentShader);
        const program = gl.createProgram();
        if (!program)
            throw new Error("Could not allocate a WebGL2 shader program");
        this.program = program;
        gl.attachShader(program, vertex);
        gl.attachShader(program, fragment);
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            throw new Error(`WebGL2 shader link failed: ${gl.getProgramInfoLog(program) || "No diagnostic available"}`);
        }
        for (const shader of this.shaders) {
            gl.detachShader(program, shader);
            gl.deleteShader(shader);
        }
        this.shaders.clear();
        const viewport = gl.getUniformLocation(program, "viewport");
        const image = gl.getUniformLocation(program, "image");
        if (viewport === null || image === null)
            throw new Error("WebGL2 shader uniforms are unavailable");
        this.viewport = viewport;
        const buffer = gl.createBuffer();
        if (!buffer)
            throw new Error("Could not allocate a WebGL2 instance buffer");
        this.instanceBuffer = buffer;
        const vertexArray = gl.createVertexArray();
        if (!vertexArray)
            throw new Error("Could not allocate a WebGL2 vertex array");
        this.vertexArray = vertexArray;
        gl.useProgram(program);
        gl.uniform1i(image, 0);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindVertexArray(vertexArray);
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        for (let attribute = 0; attribute < 4; attribute++) {
            gl.enableVertexAttribArray(attribute);
            gl.vertexAttribDivisor(attribute, 1);
        }
        gl.enable(gl.BLEND);
        gl.blendEquationSeparate(gl.FUNC_ADD, gl.FUNC_ADD);
        gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.disable(gl.DEPTH_TEST);
        gl.disable(gl.STENCIL_TEST);
        gl.disable(gl.CULL_FACE);
        gl.disable(gl.SCISSOR_TEST);
        gl.disable(gl.DITHER);
        this.resize(this.canvas.width, this.canvas.height);
    }
    compile(type, source) {
        const gl = this.gl;
        const shader = gl.createShader(type);
        if (!shader)
            throw new Error("Could not allocate a WebGL2 shader");
        this.shaders.add(shader);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
            throw new Error(`WebGL2 shader compilation failed: ${gl.getShaderInfoLog(shader) || "No diagnostic available"}`);
        }
        return shader;
    }
    assertActive() {
        if (this.disposed)
            throw new Error("WebGL2 backend is disposed");
        if (this.lostError)
            throw this.lostError;
    }
    contextLost(error) {
        if (this.disposed || this.lostError)
            return;
        this.lostError = error;
        for (const fence of this.fences)
            this.settleFence(fence, error);
        this.onFatal(error);
    }
    checkErrors(operation) {
        this.assertActive();
        const gl = this.gl;
        const code = gl.getError();
        if (code === gl.CONTEXT_LOST_WEBGL || gl.isContextLost()) {
            const error = new Error("WebGL2 context lost");
            this.contextLost(error);
            throw error;
        }
        if (code !== gl.NO_ERROR) {
            const name = code === gl.OUT_OF_MEMORY ? "OUT_OF_MEMORY" :
                code === gl.INVALID_ENUM ? "INVALID_ENUM" :
                    code === gl.INVALID_VALUE ? "INVALID_VALUE" :
                        code === gl.INVALID_OPERATION ? "INVALID_OPERATION" :
                            code === gl.INVALID_FRAMEBUFFER_OPERATION ? "INVALID_FRAMEBUFFER_OPERATION" : `0x${code.toString(16)}`;
            throw new Error(`WebGL2 ${operation} failed: ${name}`);
        }
    }
    createTexture(width, height, label) {
        this.assertActive();
        if (!positiveInteger(width) || !positiveInteger(height) ||
            width > this.textureLimit || height > this.textureLimit) {
            throw new Error(`${label} has invalid dimensions or exceeds the WebGL2 texture dimension limit`);
        }
        const gl = this.gl;
        const texture = gl.createTexture();
        if (!texture) {
            this.checkErrors("texture allocation");
            throw new Error(`Could not allocate WebGL2 texture: ${label}`);
        }
        try {
            gl.bindTexture(gl.TEXTURE_2D, texture);
            prepareUpload(gl);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            // WebGL guarantees zero initialization for null data, including untouched atlas padding.
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            this.checkErrors(`texture allocation (${label})`);
            const resource = new WebGl2Texture(this, gl, texture, width, height, () => this.assertActive(), resource => this.textures.delete(resource));
            this.textures.add(resource);
            return resource;
        }
        catch (error) {
            gl.deleteTexture(texture);
            throw error;
        }
    }
    resize(width, height) {
        this.assertActive();
        if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
            throw new Error("Invalid WebGL2 logical viewport dimensions");
        }
        if (!positiveInteger(this.canvas.width) || !positiveInteger(this.canvas.height) ||
            this.canvas.width > this.canvasLimit || this.canvas.height > this.canvasLimit) {
            throw new Error("Canvas exceeds the WebGL2 drawing buffer dimension limit");
        }
        const gl = this.gl;
        if (gl.drawingBufferWidth !== this.canvas.width || gl.drawingBufferHeight !== this.canvas.height) {
            this.checkErrors("drawing buffer resize");
            throw new Error("WebGL2 could not allocate the requested canvas drawing buffer");
        }
        gl.viewport(0, 0, this.canvas.width, this.canvas.height);
        gl.useProgram(this.program);
        gl.uniform2f(this.viewport, width, height);
        this.checkErrors("resize");
    }
    submit(instances, quadCount, batches, background) {
        this.assertActive();
        if (!Number.isSafeInteger(quadCount) || quadCount < 0 || quadCount * QUAD_STRIDE > instances.length) {
            throw new Error("Invalid WebGL2 instance data");
        }
        for (const batch of batches) {
            if (!(batch.resource instanceof WebGl2Texture) || batch.resource.owner !== this) {
                throw new Error("Texture belongs to a different rendering backend");
            }
            if (batch.resource.destroyed)
                throw new Error("WebGL2 texture is destroyed");
            if (!Number.isSafeInteger(batch.start) || !Number.isSafeInteger(batch.count) ||
                batch.start < 0 || batch.count < 0 || batch.start + batch.count > quadCount) {
                throw new Error("Invalid WebGL2 batch instance range");
            }
        }
        const gl = this.gl;
        const strideBytes = QUAD_STRIDE * Float32Array.BYTES_PER_ELEMENT;
        const usedBytes = quadCount * strideBytes;
        gl.bindVertexArray(this.vertexArray);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.instanceBuffer);
        if (!this.bufferBytes || usedBytes > this.bufferBytes) {
            const bytes = Math.max(256, instances.byteLength);
            gl.bufferData(gl.ARRAY_BUFFER, bytes, gl.DYNAMIC_DRAW);
            this.checkErrors("instance buffer allocation");
            this.bufferBytes = bytes;
        }
        if (usedBytes)
            gl.bufferSubData(gl.ARRAY_BUFFER, 0, instances, 0, quadCount * QUAD_STRIDE);
        gl.useProgram(this.program);
        gl.activeTexture(gl.TEXTURE0);
        gl.clearColor(background[0], background[1], background[2], 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
        for (const batch of batches) {
            if (!batch.count)
                continue;
            const resource = batch.resource;
            gl.bindTexture(gl.TEXTURE_2D, resource.texture);
            // WebGL2 has no baseInstance; rebase all per-instance attributes for each ordered batch.
            const offset = batch.start * strideBytes;
            gl.vertexAttribPointer(0, 4, gl.FLOAT, false, strideBytes, offset);
            gl.vertexAttribPointer(1, 4, gl.FLOAT, false, strideBytes, offset + 16);
            gl.vertexAttribPointer(2, 4, gl.FLOAT, false, strideBytes, offset + 32);
            gl.vertexAttribPointer(3, 1, gl.FLOAT, false, strideBytes, offset + 48);
            gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, batch.count);
        }
        // Upload errors are checked once per submission, not once per glyph.
        this.checkErrors("frame submission");
    }
    async idle() {
        this.checkErrors("GPU completion");
        const gl = this.gl;
        const sync = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
        if (!sync) {
            this.checkErrors("GPU fence allocation");
            throw new Error("Could not allocate a WebGL2 GPU fence");
        }
        return new Promise((resolve, reject) => {
            const fence = { sync, resolve, reject };
            this.fences.add(fence);
            try {
                gl.flush();
                this.checkErrors("GPU fence submission");
                this.pollFence(fence);
            }
            catch (error) {
                this.settleFence(fence, errorFrom(error));
            }
        });
    }
    pollFence(fence) {
        if (!this.fences.has(fence))
            return;
        try {
            this.assertActive();
            const gl = this.gl;
            if (gl.isContextLost()) {
                const error = new Error("WebGL2 context lost");
                this.contextLost(error);
                throw error;
            }
            const status = gl.clientWaitSync(fence.sync, 0, 0);
            if (status === gl.ALREADY_SIGNALED || status === gl.CONDITION_SATISFIED) {
                this.checkErrors("GPU completion");
                this.settleFence(fence);
            }
            else if (status === gl.TIMEOUT_EXPIRED) {
                fence.timer = setTimeout(() => {
                    fence.timer = undefined;
                    this.pollFence(fence);
                }, 0);
            }
            else {
                this.checkErrors("GPU fence wait");
                throw new Error("WebGL2 GPU fence wait failed");
            }
        }
        catch (error) {
            this.settleFence(fence, errorFrom(error));
        }
    }
    settleFence(fence, error) {
        if (!this.fences.delete(fence))
            return;
        if (fence.timer !== undefined)
            clearTimeout(fence.timer);
        this.gl.deleteSync(fence.sync);
        if (error)
            fence.reject(error);
        else
            fence.resolve();
    }
    dispose() {
        if (this.disposed)
            return;
        this.disposed = true;
        this.canvas.removeEventListener("webglcontextlost", this.onContextLost);
        for (const fence of this.fences)
            this.settleFence(fence, new Error("WebGL2 backend is disposed"));
        for (const texture of this.textures)
            texture.destroy();
        const gl = this.gl;
        if (this.instanceBuffer)
            gl.deleteBuffer(this.instanceBuffer);
        if (this.vertexArray)
            gl.deleteVertexArray(this.vertexArray);
        if (this.program)
            gl.deleteProgram(this.program);
        for (const shader of this.shaders)
            gl.deleteShader(shader);
        this.shaders.clear();
        this.instanceBuffer = undefined;
        this.vertexArray = undefined;
        this.program = undefined;
        this.bufferBytes = 0;
        if (!gl.isContextLost())
            gl.getExtension("WEBGL_lose_context")?.loseContext();
    }
}
//# sourceMappingURL=webgl2-backend.js.map