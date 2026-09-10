import { decodeFrame, screenText } from "./protocol.js";
import { TerminalRenderer } from "./renderer.js";
import { errorMessage } from "./validation.js";
let renderer;
let socket;
let failed = false;
let stopped = false;
let processing = false;
let drawing = false;
let frameInFlight = false;
let scheduled = false;
let needsRender = false;
let hasBlink = false;
let lastBlink = true;
let metadata;
let cells = [];
let localRevision = 0;
let pendingFrame;
let renderPromise = Promise.resolve();
let metricsTimer;
let blinkTimer;
let viewport;
const stats = {
    revision: 0, fullFrames: 0, frames: 0, presentations: 0,
    changedCells: 0, lastChangedCells: 0, discardedFrames: 0,
    imageCount: 0, textureBytes: 0, atlasGlyphs: 0, atlasBytes: 0,
    bytesReceived: 0, imageUploadBytes: 0, imagePayloadBytes: 0,
    gpu: "initializing", connected: false, warnings: [],
    fps: 0, receivedKBps: 0, workloadMBps: 0,
    captureMs: 0, rendererCpuMs: 0, preparationCpuMs: 0,
    workloadBytes: 0, outputBatches: 0, serverElapsedMs: 0,
};
let sample = { time: performance.now(), presentations: 0, bytes: 0, workload: 0 };
function postStatus(message, level = "info") {
    self.postMessage({ type: "status", message, level });
}
function send(message) {
    if (socket?.readyState === WebSocket.OPEN)
        socket.send(JSON.stringify(message));
}
function emitStats(text) {
    if (renderer && !renderer.disposed)
        Object.assign(stats, renderer.metrics());
    self.postMessage({ type: "stats", stats: { ...stats }, ...(text === undefined ? {} : { text }) });
}
function fail(error) {
    if (failed || stopped)
        return;
    failed = true;
    const message = errorMessage(error);
    stats.gpu = "error";
    stats.connected = false;
    clearInterval(metricsTimer);
    clearInterval(blinkTimer);
    socket?.close(1011, "Browser renderer failed");
    emitStats();
    postStatus(message, "error");
    renderer?.dispose();
}
self.addEventListener("error", event => {
    event.preventDefault();
    fail(event.error || new Error(event.message));
});
self.addEventListener("unhandledrejection", event => {
    event.preventDefault();
    fail(event.reason);
});
/** At most one state frame, one decode, and one GPU submission are outstanding. */
function scheduleRender() {
    needsRender = true;
    if (scheduled || drawing || processing || failed || stopped || !metadata)
        return;
    scheduled = true;
    self.requestAnimationFrame(() => {
        scheduled = false;
        if (processing || drawing || failed || stopped)
            return;
        renderPromise = drawFrame();
    });
}
async function drawFrame() {
    if (!needsRender || !metadata || !renderer)
        return;
    needsRender = false;
    drawing = true;
    const frame = pendingFrame;
    try {
        renderer.resize(metadata.columns, metadata.rows, viewport);
        const blink = Math.floor(performance.now() / 600) % 2 === 0;
        const result = renderer.render(cells, metadata, blink);
        // This is bounded completion/backpressure, not GPU readback or a GPU timing measurement.
        await renderer.idle();
        if (failed || stopped)
            return;
        lastBlink = blink;
        stats.presentations++;
        stats.rendererCpuMs = result.cpuMs;
        stats.quads = result.quads;
        stats.drawCalls = result.drawCalls;
        stats.warnings = renderer.canvasLimited
            ? [...metadata.warnings, `Canvas resolution capped by the GPU; terminal grid remains ${metadata.columns}x${metadata.rows}`]
            : metadata.warnings;
        if (frame) {
            pendingFrame = undefined;
            frameInFlight = false;
            stats.frames++;
            if (frame.full)
                stats.fullFrames++;
            stats.revision = frame.revision;
            stats.changedCells += frame.changedCells;
            stats.lastChangedCells = frame.changedCells;
            stats.captureMs = metadata.stats.captureMs;
            stats.workloadBytes = metadata.stats.workloadBytes;
            stats.outputBatches = metadata.stats.outputBatches;
            stats.serverElapsedMs = metadata.stats.elapsedMs;
            stats.columns = metadata.columns;
            stats.rows = metadata.rows;
            stats.mouseTracking = metadata.mouseTracking;
            stats.peer = metadata.peer;
            stats.history = metadata.history;
            const text = screenText(cells, metadata.columns, metadata.rows);
            self.postMessage({
                type: "geometry", columns: metadata.columns, rows: metadata.rows,
                cellWidth: metadata.cellWidth, cellHeight: metadata.cellHeight,
                mouseTracking: metadata.mouseTracking, peer: metadata.peer,
                history: metadata.history, revision: frame.revision, title: metadata.title,
                progress: metadata.progress, shellIntegration: metadata.shellIntegration,
                text, hyperlinks: metadata.hyperlinks
            });
            send({ type: "ack", revision: frame.revision });
            emitStats(text);
        }
    }
    catch (error) {
        fail(error);
    }
    finally {
        drawing = false;
        if (needsRender && !processing)
            scheduleRender();
    }
}
async function receiveFrame(buffer) {
    if (failed || stopped || !renderer)
        return;
    if (frameInFlight)
        throw new Error("Server sent a second state frame before acknowledgement");
    frameInFlight = true;
    processing = true;
    stats.bytesReceived += buffer.byteLength || 0;
    try {
        const frame = decodeFrame(buffer);
        const next = frame.metadata;
        if (!next.full && (next.baseRevision !== localRevision || next.revision <= localRevision ||
            !metadata || next.columns !== metadata.columns || next.rows !== metadata.rows)) {
            stats.discardedFrames++;
            frameInFlight = false;
            // A discarded frame must release the server's one-in-flight gate before resync.
            send({ type: "ack", revision: next.revision });
            send({ type: "resync" });
            postStatus(`Revision mismatch (local ${localRevision}, base ${next.baseRevision}); requesting a full frame`);
            return;
        }
        await renderPromise;
        if (failed || stopped)
            return;
        // No blink presentation may reference textures while this resource transaction is in progress.
        await renderer.idle();
        await renderer.updateImages(frame.images, next.retainedImages);
        if (failed || stopped)
            return;
        const preparationStart = performance.now();
        const nextCells = next.full
            ? new Array(next.columns * next.rows) : cells.slice();
        for (const cell of frame.cells)
            nextCells[cell.index] = cell;
        renderer.prepareGlyphs(nextCells);
        cells = nextCells;
        metadata = next;
        localRevision = next.revision;
        pendingFrame = { revision: next.revision, full: next.full, changedCells: frame.cells.length };
        hasBlink = cells.some(cell => cell && (cell.attributes & 16) && !(cell.attributes & 64)) ||
            (next.cursor.visible && (next.cursor.shape === 0 || next.cursor.shape % 2 === 1));
        stats.preparationCpuMs = performance.now() - preparationStart;
        needsRender = true;
    }
    finally {
        processing = false;
        if (needsRender)
            scheduleRender();
    }
}
async function initialize(message) {
    if (renderer || socket)
        throw new Error("Worker is already initialized");
    if (typeof self.requestAnimationFrame !== "function") {
        throw new Error("This browser does not support requestAnimationFrame in a dedicated OffscreenCanvas worker");
    }
    postStatus("Loading terminal font and initializing renderer...");
    renderer = await TerminalRenderer.create(message.canvas, message.scale, fail, message.font, message.renderer);
    if (failed || stopped) {
        renderer?.dispose();
        return;
    }
    stats.gpu = "ready";
    stats.backingScale = message.scale;
    emitStats();
    const rendererName = renderer.backend.kind === "webgpu" ? "WebGPU" : "WebGL2";
    if (renderer.fallbackReason)
        postStatus(`Using WebGL2: ${renderer.fallbackReason}`);
    postStatus(`${rendererName} ready. Attaching terminal view...`);
    const url = new URL(message.url);
    if (!["ws:", "wss:"].includes(url.protocol)) {
        throw new Error("The terminal WebSocket URL must use ws: or wss:");
    }
    socket = new WebSocket(url);
    socket.binaryType = "arraybuffer";
    socket.addEventListener("open", () => {
        if (failed || stopped)
            return;
        stats.connected = true;
        self.postMessage({ type: "connected" });
        postStatus(`Connected · ${rendererName} worker · server-authoritative cells and graphics`, "ready");
        emitStats();
    });
    socket.addEventListener("message", event => {
        if (!(event.data instanceof ArrayBuffer)) {
            fail(new Error(`Expected binary HWT1 frame, received ${String(event.data).slice(0, 200)}`));
            return;
        }
        receiveFrame(event.data).catch(fail);
    });
    socket.addEventListener("error", () => fail(new Error("WebSocket connection failed; verify the demo server is running")));
    socket.addEventListener("close", event => {
        stats.connected = false;
        if (!failed && !stopped) {
            stats.gpu = "stopped";
            stats.fps = 0;
            postStatus(`View disconnected (${event.code}${event.reason ? `: ${event.reason}` : ""}). Attach another view to reconnect.`, "error");
            emitStats();
            stopped = true;
            clearInterval(metricsTimer);
            clearInterval(blinkTimer);
            renderer?.dispose();
            self.postMessage({ type: "disconnected" });
        }
    });
    metricsTimer = setInterval(() => {
        const now = performance.now();
        const seconds = (now - sample.time) / 1000;
        stats.fps = (stats.presentations - sample.presentations) / seconds;
        stats.receivedKBps = (stats.bytesReceived - sample.bytes) / seconds / 1000;
        stats.workloadMBps = Math.max(0, stats.workloadBytes - sample.workload) / seconds / 1000000;
        sample = { time: now, presentations: stats.presentations, bytes: stats.bytesReceived, workload: stats.workloadBytes };
        emitStats();
    }, 1000);
    blinkTimer = setInterval(() => {
        const blinkOn = Math.floor(performance.now() / 600) % 2 === 0;
        if (hasBlink && blinkOn !== lastBlink)
            scheduleRender();
    }, 100);
}
self.addEventListener("message", event => {
    const message = event.data;
    if (message.type === "init") {
        initialize(message).catch(fail);
    }
    else if (message.type === "stop") {
        stopped = true;
        clearInterval(metricsTimer);
        clearInterval(blinkTimer);
        socket?.close(1000, "View detached");
        renderer?.dispose();
        self.close();
    }
    else if (message.type === "viewport" && !failed && !stopped) {
        if (!Number.isFinite(message.width) || !Number.isFinite(message.height) || message.width < 0 || message.height < 0) {
            fail(new Error("Invalid mounted viewport dimensions"));
            return;
        }
        if (viewport?.width === message.width && viewport?.height === message.height)
            return;
        viewport = { width: message.width, height: message.height };
        scheduleRender();
    }
    else if (message.type === "command" && !failed && !stopped) {
        send(message.command);
    }
});
//# sourceMappingURL=terminal-worker.js.map