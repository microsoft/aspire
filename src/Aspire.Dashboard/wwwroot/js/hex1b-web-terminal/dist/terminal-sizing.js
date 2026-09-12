export const MIN_FONT_SIZE = 8;
export const MAX_FONT_SIZE = 32;
const NATIVE_FONT_SIZE = 16;
export function dimensions(columns, rows) {
    if (!Number.isInteger(columns) || columns < 20 || columns > 300 ||
        !Number.isInteger(rows) || rows < 10 || rows > 100) {
        throw new RangeError("Requested grid must be 20-300 columns by 10-100 rows");
    }
    return { columns, rows };
}
export function normalizeSizing(sizing = { mode: "auto" }, previousFontSize = NATIVE_FONT_SIZE) {
    if (!sizing || !["auto", "fixed"].includes(sizing.mode)) {
        throw new TypeError("Sizing mode must be 'auto' or 'fixed'");
    }
    const fontSize = sizing.fontSize ?? previousFontSize;
    if (!Number.isInteger(fontSize) || fontSize < MIN_FONT_SIZE || fontSize > MAX_FONT_SIZE) {
        throw new RangeError(`Font size must be an integer from ${MIN_FONT_SIZE} to ${MAX_FONT_SIZE}`);
    }
    return sizing.mode === "fixed"
        ? { mode: "fixed", fontSize, ...dimensions(sizing.columns, sizing.rows) }
        : { mode: "auto", fontSize };
}
export function requestedGrid(size, geometry, sizing) {
    if (sizing.mode === "fixed")
        return { columns: sizing.columns, rows: sizing.rows };
    if (size.width <= 0 || size.height <= 0)
        return null;
    const scale = sizing.fontSize / NATIVE_FONT_SIZE;
    return {
        columns: Math.max(20, Math.min(300, Math.floor(size.width / (geometry.cellWidth * scale)))),
        rows: Math.max(10, Math.min(100, Math.floor(size.height / (geometry.cellHeight * scale))))
    };
}
export function fittedScale(size, geometry, isPrimary, sizing) {
    const maximum = isPrimary && sizing.mode === "auto" ? sizing.fontSize / NATIVE_FONT_SIZE : Infinity;
    return Math.max(0, Math.min(maximum, size.width / (geometry.columns * geometry.cellWidth), size.height / (geometry.rows * geometry.cellHeight)));
}
//# sourceMappingURL=terminal-sizing.js.map