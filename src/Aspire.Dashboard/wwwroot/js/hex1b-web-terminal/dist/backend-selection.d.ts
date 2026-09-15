import type { RenderBackend } from "./render-backend.js";
import type { TerminalRendererPreference } from "./types.js";
export declare function createRenderBackend(canvas: OffscreenCanvas, onFatal: (error: Error) => void, preference?: TerminalRendererPreference): Promise<{
    backend: RenderBackend;
    fallbackReason?: string;
}>;
//# sourceMappingURL=backend-selection.d.ts.map