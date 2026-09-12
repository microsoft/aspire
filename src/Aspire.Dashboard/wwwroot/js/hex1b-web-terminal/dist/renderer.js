import { LIMITS } from "./protocol.js";
import { loadFont, measureFont, normalizeFont } from "./terminal-font.js";
import { createRenderBackend } from "./backend-selection.js";
import { QUAD_STRIDE } from "./render-backend.js";
const CELL_WIDTH = 10;
const CELL_HEIGHT = 20;
const MAX_QUADS = 1024 * 1024;
const MAX_GLYPHS = 16384;
const MAX_GLYPH_KEY_UNITS = 1024 * 1024;
const STRIDE = QUAD_STRIDE;
const WHITE = [1, 1, 1, 1];
function rgba(packed) {
    return [
        (packed & 255) / 255,
        ((packed >>> 8) & 255) / 255,
        ((packed >>> 16) & 255) / 255,
        ((packed >>> 24) & 255) / 255,
    ];
}
function glyphKey(cell) {
    return `${cell.attributes & 5}/${cell.width}/${cell.text}`;
}
/** Shelf packer. Plans are computed before mutating the atlas used by a submitted frame. */
function packGlyphs(glyphs, size, scale, initial = { x: 0, y: 0, rowHeight: 0 }) {
    let { x, y, rowHeight } = initial;
    const placements = [];
    for (const [key, cell] of glyphs) {
        const width = Math.ceil(cell.width * CELL_WIDTH * scale) + 4;
        const height = Math.ceil(CELL_HEIGHT * scale) + 4;
        if (width > size || height > size)
            return null;
        if (x + width > size) {
            x = 0;
            y += rowHeight;
            rowHeight = 0;
        }
        if (y + height > size)
            return null;
        placements.push({ key, cell, x, y, width, height });
        x += width;
        rowHeight = Math.max(rowHeight, height);
    }
    return { placements, shelf: { x, y, rowHeight } };
}
/** Shared instanced-quad preparation; Canvas2D rasterizes reusable glyphs for either backend. */
export class TerminalRenderer {
    canvas;
    scale;
    backingScale;
    backend;
    fallbackReason;
    fontConfiguration;
    fontMetrics;
    images;
    glyphs;
    imageUploadBytes;
    imagePayloadBytes;
    glyphUploadBytes;
    atlasRebuilds;
    textureBytes;
    instances;
    disposed;
    columns;
    rows;
    // Initialized by create() before the renderer can prepare or submit frames.
    font;
    rasterCanvas;
    raster;
    atlas;
    glyphKeyUnits = 0;
    shelf = { x: 0, y: 0, rowHeight: 0 };
    canvasLimited = false;
    width = 0;
    height = 0;
    quadCount = 0;
    batches = [];
    static async create(canvas, scale, onFatal, font, preference = "auto") {
        const normalizedFont = normalizeFont(font);
        if (!Number.isFinite(scale) || scale < 0.5 || scale > 3)
            throw new Error("Invalid backing scale");
        const { backend, fallbackReason } = await createRenderBackend(canvas, onFatal, preference);
        const renderer = new TerminalRenderer(canvas, scale, backend, normalizedFont);
        renderer.fallbackReason = fallbackReason;
        try {
            await renderer.initialize();
            return renderer;
        }
        catch (error) {
            renderer.dispose();
            throw error;
        }
    }
    constructor(canvas, scale, backend, font) {
        if (!Number.isFinite(scale) || scale < 0.5 || scale > 3)
            throw new Error("Invalid backing scale");
        this.canvas = canvas;
        this.scale = scale;
        this.backingScale = scale;
        this.backend = backend;
        this.fontConfiguration = font;
        this.fontMetrics = new Map();
        this.images = new Map();
        this.glyphs = new Map();
        this.imageUploadBytes = 0;
        this.imagePayloadBytes = 0;
        this.glyphUploadBytes = 0;
        this.atlasRebuilds = 0;
        this.textureBytes = 0;
        this.instances = new Float32Array(4096 * STRIDE);
        this.disposed = false;
        this.columns = 0;
        this.rows = 0;
    }
    async initialize() {
        this.font = await loadFont(this.fontConfiguration);
        this.rasterCanvas = new OffscreenCanvas(1, 1);
        const raster = this.rasterCanvas.getContext("2d", { willReadFrequently: true });
        if (!raster)
            throw new Error("Worker glyph rasterization is unavailable");
        this.raster = raster;
        this.resetAtlas(Math.min(2048, this.backend.maxTextureDimension2D));
    }
    createTexture(width, height, label) {
        return this.backend.createTexture(width, height, label);
    }
    resetAtlas(size) {
        this.atlas?.destroy();
        this.atlas = this.createTexture(size, size, "Glyph atlas");
        this.glyphs.clear();
        this.glyphKeyUnits = 0;
        this.shelf = { x: 0, y: 0, rowHeight: 0 };
    }
    resize(columns, rows, viewport) {
        const width = columns * CELL_WIDTH;
        const height = rows * CELL_HEIGHT;
        const limit = this.backend.maxCanvasDimension2D;
        const requested = Math.min(this.scale, viewport ? viewport.width / width : Infinity, viewport ? viewport.height / height : Infinity);
        this.canvasLimited = width * requested > limit || height * requested > limit;
        this.backingScale = Math.min(requested, limit / width, limit / height);
        const backingWidth = Math.max(1, Math.min(limit, Math.ceil(width * this.backingScale)));
        const backingHeight = Math.max(1, Math.min(limit, Math.ceil(height * this.backingScale)));
        if (this.columns === columns && this.rows === rows && this.canvas.width === backingWidth && this.canvas.height === backingHeight)
            return;
        this.columns = columns;
        this.rows = rows;
        this.width = width;
        this.height = height;
        this.canvas.width = backingWidth;
        this.canvas.height = backingHeight;
        this.backend.resize(width, height);
    }
    /** Call only between submissions. Missing/over-budget resources terminate the session. */
    async updateImages(incoming, retainedKeys) {
        const retained = new Set(retainedKeys);
        const replacements = new Map(incoming.map(image => [image.key, image]));
        let projectedBytes = 0;
        for (const key of retained) {
            const image = replacements.get(key) || this.images.get(key);
            if (!image)
                throw new Error(`Missing retained image resource: ${key}`);
            projectedBytes += image.width * image.height * 4;
        }
        if (projectedBytes > LIMITS.textureBytes)
            throw new Error("Retained images exceed the 256 MiB texture budget");
        for (const [key, image] of this.images) {
            if (!retained.has(key) || replacements.has(key)) {
                image.destroy();
                this.textureBytes -= image.width * image.height * 4;
                this.images.delete(key);
            }
        }
        for (const image of incoming) {
            const resource = this.createTexture(image.width, image.height, `Image ${image.key}`);
            try {
                if (image.format === "rgba") {
                    resource.writePixels(image.bytes, image.width, image.height);
                }
                else {
                    // Check IHDR before decoding so a tiny PNG cannot claim unbounded decoded dimensions.
                    const png = new DataView(image.bytes.buffer, image.bytes.byteOffset, image.bytes.byteLength);
                    if (png.byteLength < 33 || png.getUint32(0) !== 0x89504e47 || png.getUint32(4) !== 0x0d0a1a0a ||
                        png.getUint32(8) !== 13 || png.getUint32(12) !== 0x49484452 ||
                        png.getUint32(16) !== image.width || png.getUint32(20) !== image.height) {
                        throw new Error(`PNG header dimensions do not match resource ${image.key}`);
                    }
                    const bitmap = await createImageBitmap(new Blob([image.bytes], { type: "image/png" }), {
                        premultiplyAlpha: "none",
                        colorSpaceConversion: "none",
                    });
                    try {
                        if (bitmap.width !== image.width || bitmap.height !== image.height)
                            throw new Error("Decoded PNG dimension mismatch");
                        resource.writeBitmap(bitmap);
                    }
                    finally {
                        bitmap.close();
                    }
                }
                this.images.set(image.key, resource);
                this.textureBytes += image.width * image.height * 4;
                this.imageUploadBytes += image.width * image.height * 4;
                this.imagePayloadBytes += image.byteLength;
            }
            catch (error) {
                resource.destroy();
                throw error;
            }
        }
    }
    prepareGlyphs(cells) {
        const visible = new Map();
        for (const cell of cells) {
            if (!cell || !cell.width || !cell.text.trim() || (cell.attributes & 64))
                continue;
            visible.set(glyphKey(cell), cell);
        }
        const keyUnits = (glyphs) => [...glyphs.keys()].reduce((total, key) => total + key.length, 0);
        if (visible.size > MAX_GLYPHS || keyUnits(visible) > MAX_GLYPH_KEY_UNITS) {
            throw new Error("Visible glyph metadata exceeds the bounded glyph cache");
        }
        const missing = new Map([...visible].filter(([key]) => !this.glyphs.has(key)));
        if (!missing.size)
            return;
        const metadataFits = this.glyphs.size + missing.size <= MAX_GLYPHS &&
            this.glyphKeyUnits + keyUnits(missing) <= MAX_GLYPH_KEY_UNITS;
        let plan = metadataFits ? packGlyphs(missing, this.atlas.width, this.scale, this.shelf) : null;
        if (!plan) {
            let size = this.atlas.width;
            const maxSize = Math.min(4096, this.backend.maxTextureDimension2D);
            while (!(plan = packGlyphs(visible, size, this.scale)) && size < maxSize) {
                size = Math.min(size * 2, maxSize);
            }
            if (!plan)
                throw new Error("Visible glyphs exceed the bounded 4096² mask atlas; reduce the grid or backing scale");
            this.resetAtlas(size);
            this.atlasRebuilds++;
        }
        for (const placement of plan.placements)
            this.uploadGlyph(placement);
        this.shelf = plan.shelf;
    }
    uploadGlyph({ key, cell, x, y, width, height }) {
        const scale = this.scale;
        const raster = this.raster;
        const style = cell.attributes & 5;
        let metrics = this.fontMetrics.get(style);
        if (!metrics) {
            metrics = measureFont(raster, this.font.cssFamily, style, scale, CELL_WIDTH, CELL_HEIGHT);
            this.fontMetrics.set(style, metrics);
        }
        this.rasterCanvas.width = width;
        this.rasterCanvas.height = height;
        raster.font = metrics.font;
        raster.textBaseline = "alphabetic";
        raster.textAlign = "left";
        raster.fillStyle = "white";
        // One transform per font style, not per glyph: borders remain font outlines,
        // and graphemes are clipped to their server-owned span without individual stretching.
        raster.setTransform(metrics.xScale, 0, 0, metrics.yScale, 2, 2 + metrics.baseline);
        raster.fillText(cell.text, 0, 0);
        const pixels = raster.getImageData(0, 0, width, height);
        let colored = false;
        for (let i = 0; i < pixels.data.length; i += 4) {
            if (pixels.data[i + 3] && (pixels.data[i] !== pixels.data[i + 1] || pixels.data[i + 1] !== pixels.data[i + 2])) {
                colored = true;
                break;
            }
        }
        this.atlas.writePixels(pixels.data, width, height, x, y);
        this.glyphUploadBytes += width * height * 4;
        this.glyphs.set(key, {
            colored,
            u0: (x + 2) / this.atlas.width,
            v0: (y + 2) / this.atlas.height,
            u1: (x + 2 + cell.width * CELL_WIDTH * scale) / this.atlas.width,
            v1: (y + 2 + CELL_HEIGHT * scale) / this.atlas.height,
        });
        this.glyphKeyUnits += key.length;
    }
    /** Clip geometry and UVs together. Only adjacent compatible textures may coalesce. */
    quad(resource, x, y, width, height, color, mode = 0, uv = [0, 0, 1, 1], clip = [0, 0, this.width, this.height]) {
        if (width <= 0 || height <= 0 || color[3] <= 0)
            return;
        const left = Math.max(0, x, clip[0]);
        const top = Math.max(0, y, clip[1]);
        const right = Math.min(this.width, x + width, clip[0] + clip[2]);
        const bottom = Math.min(this.height, y + height, clip[1] + clip[3]);
        if (right <= left || bottom <= top)
            return;
        if (this.quadCount >= MAX_QUADS)
            throw new Error("Frame exceeds bounded quad budget");
        const offset = this.quadCount * STRIDE;
        if (offset + STRIDE > this.instances.length) {
            const grown = new Float32Array(Math.min(this.instances.length * 2, MAX_QUADS * STRIDE));
            grown.set(this.instances);
            this.instances = grown;
        }
        const du = uv[2] - uv[0];
        const dv = uv[3] - uv[1];
        this.instances.set([
            left, top, right - left, bottom - top,
            uv[0] + (left - x) / width * du,
            uv[1] + (top - y) / height * dv,
            uv[0] + (right - x) / width * du,
            uv[1] + (bottom - y) / height * dv,
            ...color, mode, 0, 0, 0,
        ], offset);
        const last = this.batches[this.batches.length - 1];
        if (last?.resource === resource)
            last.count++;
        else
            this.batches.push({ resource, start: this.quadCount, count: 1 });
        this.quadCount++;
    }
    solid(x, y, width, height, color) {
        this.quad(this.atlas, x, y, width, height, color);
    }
    placement(placement) {
        const image = this.images.get(placement.key);
        if (!image)
            throw new Error(`Placement texture is missing: ${placement.key}`);
        const { sourceX: sx, sourceY: sy, sourceWidth: sw, sourceHeight: sh } = placement;
        if (!sw || !sh || !placement.width || !placement.height)
            return;
        // Clip out-of-texture source regions in destination space instead of stretching edge texels.
        const sourceLeft = placement.x + Math.max(0, -sx) / sw * placement.width;
        const sourceTop = placement.y + Math.max(0, -sy) / sh * placement.height;
        const sourceRight = placement.x + Math.min(sw, image.width - sx) / sw * placement.width;
        const sourceBottom = placement.y + Math.min(sh, image.height - sy) / sh * placement.height;
        const left = Math.max(sourceLeft, placement.clipX);
        const top = Math.max(sourceTop, placement.clipY);
        const right = Math.min(sourceRight, placement.clipX + placement.clipWidth);
        const bottom = Math.min(sourceBottom, placement.clipY + placement.clipHeight);
        this.quad(image, placement.x, placement.y, placement.width, placement.height, WHITE, 2, [sx / image.width, sy / image.height, (sx + sw) / image.width, (sy + sh) / image.height], [left, top, Math.max(0, right - left), Math.max(0, bottom - top)]);
    }
    decorations(cell, x, y, width, foreground) {
        if (cell.attributes & 128)
            this.solid(x, y + 10, width, 1, foreground);
        if (cell.attributes & 256)
            this.solid(x, y + 1, width, 1, foreground);
        const style = cell.underlineStyle || (cell.attributes & 8 ? 1 : 0);
        const color = rgba(cell.underlineColor);
        if (style === 1)
            this.solid(x, y + 18, width, 1, color);
        else if (style === 2) {
            this.solid(x, y + 16, width, 1, color);
            this.solid(x, y + 18, width, 1, color);
        }
        else if (style === 3) {
            for (let dx = 0; dx < width; dx++) {
                this.solid(x + dx, y + 17 + Math.round(Math.sin((x + dx) * Math.PI / 4)), 1, 1, color);
            }
        }
        else if (style === 4 || style === 5) {
            const step = style === 4 ? 2 : 5;
            const segment = style === 4 ? 1 : 3;
            for (let dx = 0; dx < width; dx += step)
                this.solid(x + dx, y + 18, Math.min(segment, width - dx), 1, color);
        }
    }
    render(cells, metadata, blinkOn) {
        const start = performance.now();
        this.quadCount = 0;
        this.batches = [];
        const placements = metadata.placements.map((placement, order) => ({
            placement,
            order,
            z: placement.kind === "sixel" ? -1 : placement.z,
        })).sort((a, b) => a.z - b.z || a.order - b.order);
        for (const item of placements)
            if (item.z < -1073741824)
                this.placement(item.placement);
        for (let i = 0; i < cells.length; i++) {
            const cell = cells[i];
            if (!cell)
                continue;
            this.solid((i % this.columns) * CELL_WIDTH, Math.floor(i / this.columns) * CELL_HEIGHT, CELL_WIDTH, CELL_HEIGHT, rgba(cell.background));
        }
        for (const item of placements)
            if (item.z >= -1073741824 && item.z < 0)
                this.placement(item.placement);
        for (let i = 0; i < cells.length; i++) {
            const cell = cells[i];
            if (!cell || !cell.width || (cell.attributes & 64) || ((cell.attributes & 16) && !blinkOn))
                continue;
            const x = (i % this.columns) * CELL_WIDTH;
            const y = Math.floor(i / this.columns) * CELL_HEIGHT;
            const width = Math.min(cell.width * CELL_WIDTH, this.width - x);
            const foreground = rgba(cell.foreground);
            const glyph = this.glyphs.get(glyphKey(cell));
            if (glyph) {
                const tint = glyph.colored ? (cell.attributes & 2 ? [0.5, 0.5, 0.5, 1] : WHITE) : foreground;
                this.quad(this.atlas, x, y, cell.width * CELL_WIDTH, CELL_HEIGHT, tint, glyph.colored ? 2 : 1, [glyph.u0, glyph.v0, glyph.u1, glyph.v1]);
            }
            // Reverse and dim are already reflected in server-projected colors.
            this.decorations(cell, x, y, width, foreground);
        }
        for (const item of placements)
            if (item.z >= 0)
                this.placement(item.placement);
        const cursor = metadata.cursor;
        const cursorBlink = cursor.shape === 0 || cursor.shape % 2 === 1;
        if (cursor.visible && (!cursorBlink || blinkOn) && cursor.x >= 0 && cursor.x < this.columns && cursor.y >= 0 && cursor.y < this.rows) {
            const cell = cells[cursor.y * this.columns + cursor.x];
            const color = rgba(cell?.foreground ?? 0xffffffff);
            const x = cursor.x * CELL_WIDTH;
            const y = cursor.y * CELL_HEIGHT;
            if (cursor.shape === 3 || cursor.shape === 4)
                this.solid(x, y + 18, CELL_WIDTH, 2, color);
            else if (cursor.shape === 5 || cursor.shape === 6)
                this.solid(x, y, 2, CELL_HEIGHT, color);
            else {
                color[3] *= 0.55;
                this.solid(x, y, CELL_WIDTH, CELL_HEIGHT, color);
            }
        }
        const base = rgba(metadata.defaultBackground ?? cells[0]?.background ?? 0xff000000);
        this.backend.submit(this.instances, this.quadCount, this.batches, base);
        return { cpuMs: performance.now() - start, quads: this.quadCount, drawCalls: this.batches.length };
    }
    metrics() {
        return {
            renderer: this.backend.kind,
            rendererFallbackReason: this.fallbackReason,
            fontFamily: this.font.family,
            rasterScale: this.scale,
            backingScale: this.backingScale,
            backingWidth: this.canvas.width,
            backingHeight: this.canvas.height,
            imageCount: this.images.size,
            textureBytes: this.textureBytes,
            atlasGlyphs: this.glyphs.size,
            atlasBytes: this.atlas.width * this.atlas.height * 4,
            atlasRebuilds: this.atlasRebuilds,
            imageUploadBytes: this.imageUploadBytes,
            imagePayloadBytes: this.imagePayloadBytes,
            glyphUploadBytes: this.glyphUploadBytes,
            instanceBufferBytes: this.backend.instanceBufferBytes,
        };
    }
    async idle() {
        await this.backend.idle();
    }
    dispose() {
        if (this.disposed)
            return;
        this.disposed = true;
        for (const image of this.images.values())
            image.destroy();
        this.images.clear();
        this.atlas?.destroy();
        this.font?.dispose();
        this.fontMetrics.clear();
        this.backend.dispose();
    }
}
//# sourceMappingURL=renderer.js.map