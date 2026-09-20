import { RendererUnavailableError } from "./render-backend.js";
import { normalizeRenderer } from "./renderer-options.js";
import { WebGpuBackend } from "./webgpu-backend.js";
import { WebGl2Backend } from "./webgl2-backend.js";
export async function createRenderBackend(canvas, onFatal, preference = "auto") {
    const requested = normalizeRenderer(preference);
    if (requested === "webgl2")
        return { backend: await WebGl2Backend.create(canvas, onFatal) };
    try {
        return { backend: await WebGpuBackend.create(canvas, onFatal) };
    }
    catch (error) {
        if (requested !== "auto" || !(error instanceof RendererUnavailableError))
            throw error;
        try {
            return { backend: await WebGl2Backend.create(canvas, onFatal), fallbackReason: error.message };
        }
        catch (fallbackError) {
            throw new AggregateError([error, fallbackError], `WebGPU unavailable (${error.message}); WebGL2 initialization failed: ${fallbackError instanceof Error ? fallbackError.message : String(fallbackError)}`);
        }
    }
}
//# sourceMappingURL=backend-selection.js.map