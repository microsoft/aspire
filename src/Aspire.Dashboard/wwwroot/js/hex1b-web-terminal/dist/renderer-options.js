export function normalizeRenderer(value = "auto") {
    if (value === "auto" || value === "webgpu" || value === "webgl2")
        return value;
    throw new TypeError('renderer must be "auto", "webgpu", or "webgl2"');
}
//# sourceMappingURL=renderer-options.js.map