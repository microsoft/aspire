import { isRecord } from "./validation.js";
// Binary validation is deliberately independent of the GPU and the transport.
export const LIMITS = Object.freeze({
    commandBytes: 64 * 1024,
    frameBytes: 96 * 1024 * 1024,
    metadataBytes: 8 * 1024 * 1024,
    cells: 262144,
    images: 4096,
    placements: 16384,
    textureBytes: 256 * 1024 * 1024,
});
const utf8 = new TextDecoder("utf-8", { fatal: true });
const utf8Encoder = new TextEncoder();
export function assertCommandSize(command) {
    if (utf8Encoder.encode(JSON.stringify(command)).byteLength > LIMITS.commandBytes)
        throw new RangeError("Terminal input exceeds the 64 KiB message limit. Send a smaller amount of text.");
}
function integer(value, name, min = 0, max = Number.MAX_SAFE_INTEGER) {
    if (typeof value !== "number" || !Number.isSafeInteger(value) || value < min || value > max) {
        throw new Error(`Invalid ${name}: ${String(value)}`);
    }
    return value;
}
function array(value, name, limit) {
    if (!Array.isArray(value) || value.length > limit)
        throw new Error(`Invalid ${name}`);
}
function key(value) {
    if (typeof value !== "string" || !value.length || value.length > 1024) {
        throw new Error("Invalid image key");
    }
}
function rowId(value, name) {
    if (typeof value !== "string" || !/^[1-9][0-9]{0,18}$/u.test(value) || BigInt(value) > 9223372036854775807n) {
        throw new Error(`Invalid ${name}`);
    }
}
export function validateHistory(history, columns, rows) {
    if (history === null)
        return;
    if (!isRecord(history))
        throw new Error("Missing history metadata");
    rowId(history.generation, "history generation");
    if (history.buffer !== "main" && history.buffer !== "alternate")
        throw new Error("Invalid history buffer");
    const totalRows = integer(history.totalRows, "history total rows", rows, 2147483647);
    const liveTop = integer(history.liveTop, "history live top", 0, totalRows - rows);
    if (liveTop !== totalRows - rows)
        throw new Error("Inconsistent history extent");
    integer(history.top, "history top", 0, liveTop);
    if (typeof history.following !== "boolean" || (history.following && history.top !== history.liveTop)) {
        throw new Error("Invalid history following state");
    }
    integer(history.requestId, "viewport request id");
    array(history.rowIds, "viewport row ids", rows);
    if (history.rowIds.length !== rows || new Set(history.rowIds).size !== rows)
        throw new Error("Invalid viewport row ids");
    for (const id of history.rowIds)
        rowId(id, "viewport row id");
    const selection = history.selection;
    if (!isRecord(selection) || typeof selection.status !== "string" ||
        !["none", "valid", "invalidated"].includes(selection.status))
        throw new Error("Invalid selection status");
    integer(selection.requestId, "selection request id");
    if (typeof selection.mode !== "string" || !["character", "word", "line", "rectangle"].includes(selection.mode))
        throw new Error("Invalid selection mode");
    array(selection.ranges, "selection ranges", rows);
    let previousRow = -1;
    for (const range of selection.ranges) {
        if (!isRecord(range))
            throw new Error("Invalid selection range");
        const row = integer(range.row, "selection range row", previousRow + 1, rows - 1);
        const startColumn = integer(range.startColumn, "selection start column", 0, columns - 1);
        integer(range.endColumn, "selection end column", startColumn + 1, columns);
        previousRow = row;
    }
    validateSelectionText(selection);
    if (selection.status !== "valid" && selection.ranges.length)
        throw new Error("Inactive selection has highlight ranges");
    if (history.copy !== null) {
        if (!isRecord(history.copy))
            throw new Error("Missing copy metadata");
        integer(history.copy.requestId, "copy request id", 1);
        validateSelectionText(history.copy);
    }
}
function validateSelectionText(selection) {
    if (typeof selection.status !== "string" || !["none", "valid", "invalidated"].includes(selection.status) ||
        (selection.status === "valid"
            ? typeof selection.text !== "string" || selection.text.length > LIMITS.metadataBytes
            : selection.text !== null)) {
        throw new Error("Invalid selection text");
    }
}
function validateMetadata(metadata) {
    if (!isRecord(metadata) || metadata.version !== 1 || typeof metadata.full !== "boolean") {
        throw new Error("Unsupported frame metadata version");
    }
    integer(metadata.revision, "revision", 1);
    integer(metadata.baseRevision, "base revision");
    const columns = integer(metadata.columns, "columns", 1, 1024);
    const rows = integer(metadata.rows, "rows", 1, 512);
    if (typeof metadata.mouseTracking !== "number" || ![0, 9, 1000, 1002, 1003].includes(metadata.mouseTracking)) {
        throw new Error("Unsupported mouse tracking mode");
    }
    if (!isRecord(metadata.peer) || typeof metadata.peer.isPrimary !== "boolean")
        throw new Error("Invalid peer state");
    for (const field of ["id", "primaryId"]) {
        const id = metadata.peer[field];
        if (id !== null && (typeof id !== "string" || !id.length || id.length > 256)) {
            throw new Error(`Invalid peer ${field}`);
        }
    }
    if (metadata.peer.id !== null && metadata.peer.isPrimary !== (metadata.peer.id === metadata.peer.primaryId)) {
        throw new Error("Inconsistent primary peer state");
    }
    integer(columns * rows, "cell count", 1, LIMITS.cells);
    validateHistory(metadata.history, columns, rows);
    if (metadata.cellWidth !== 10 || metadata.cellHeight !== 20) {
        throw new Error("This spike requires server geometry of 10 × 20 logical pixels");
    }
    for (const field of ["defaultBackground", "defaultForeground"]) {
        if (metadata[field] !== undefined)
            integer(metadata[field], field, 0, 0xffffffff);
    }
    if (!isRecord(metadata.cursor) || typeof metadata.cursor.visible !== "boolean")
        throw new Error("Invalid cursor");
    integer(metadata.cursor.x, "cursor x", -1, 1024);
    integer(metadata.cursor.y, "cursor y", -1, 512);
    const shapes = ["Default", "BlinkingBlock", "SteadyBlock", "BlinkingUnderline", "SteadyUnderline", "BlinkingBar", "SteadyBar"];
    if (typeof metadata.cursor.shape === "string")
        metadata.cursor.shape = shapes.indexOf(metadata.cursor.shape);
    integer(metadata.cursor.shape, "cursor shape", 0, 6);
    array(metadata.images, "images", LIMITS.images);
    array(metadata.retainedImages, "retained image keys", LIMITS.images);
    array(metadata.placements, "placements", LIMITS.placements);
    array(metadata.warnings, "warnings", 256);
    if (metadata.warnings.some(w => typeof w !== "string"))
        throw new Error("Invalid warning");
    if (!isRecord(metadata.stats))
        throw new Error("Invalid server metrics");
    for (const field of ["workloadBytes", "outputBatches", "captureMs", "elapsedMs"]) {
        const metric = metadata.stats[field];
        if (typeof metric !== "number" || !Number.isFinite(metric) || metric < 0) {
            throw new Error(`Invalid server metric ${field}`);
        }
    }
    const retained = new Set();
    for (const imageKey of metadata.retainedImages) {
        key(imageKey);
        if (retained.has(imageKey))
            throw new Error("Duplicate retained image key");
        retained.add(imageKey);
    }
    const imageKeys = new Set();
    let decodedImageBytes = 0;
    for (const image of metadata.images) {
        if (!isRecord(image))
            throw new Error("Invalid image");
        key(image.key);
        if (imageKeys.has(image.key) || !retained.has(image.key))
            throw new Error("Inconsistent new image keys");
        imageKeys.add(image.key);
        const width = integer(image.width, "image width", 1, 16384);
        const height = integer(image.height, "image height", 1, 16384);
        const byteLength = integer(image.byteLength, "image byte length", 1, LIMITS.frameBytes);
        if (image.format !== "rgba" && image.format !== "png")
            throw new Error("Unsupported image format");
        if (image.format === "rgba" && byteLength !== width * height * 4) {
            throw new Error("RGBA image size mismatch");
        }
        decodedImageBytes += width * height * 4;
        if (decodedImageBytes > LIMITS.textureBytes)
            throw new Error("New images exceed decoded texture budget");
    }
    for (const placement of metadata.placements) {
        if (!isRecord(placement))
            throw new Error("Invalid placement");
        key(placement.key);
        if (!retained.has(placement.key))
            throw new Error("Placement references an unretained image");
        if (placement.kind !== "kgp" && placement.kind !== "sixel")
            throw new Error("Invalid placement kind");
        for (const field of ["x", "y", "width", "height", "sourceX", "sourceY", "sourceWidth", "sourceHeight", "clipX", "clipY", "clipWidth", "clipHeight", "z"]) {
            const coordinate = placement[field];
            if (typeof coordinate !== "number" || !Number.isFinite(coordinate) || Math.abs(coordinate) > 2147483648) {
                throw new Error(`Invalid placement ${field}`);
            }
        }
        for (const field of ["width", "height", "sourceWidth", "sourceHeight", "clipWidth", "clipHeight"]) {
            const coordinate = placement[field];
            if (typeof coordinate !== "number" || coordinate < 0)
                throw new Error(`Negative placement ${field}`);
        }
    }
}
/** Decode one complete, experimental HWT1 message, checking every boundary before reading. */
export function decodeFrame(buffer) {
    if (!(buffer instanceof ArrayBuffer) || buffer.byteLength > LIMITS.frameBytes) {
        throw new Error("Invalid or oversized binary frame");
    }
    const view = new DataView(buffer);
    let offset = 0;
    const requireBytes = (count) => {
        if (count < 0 || count > view.byteLength - offset)
            throw new Error("Truncated HWT1 frame");
    };
    const u32 = () => { requireBytes(4); const n = view.getUint32(offset, true); offset += 4; return n; };
    if (u32() !== 0x31545748)
        throw new Error("Unsupported frame magic (expected HWT1)");
    const metadataLength = integer(u32(), "metadata length", 2, LIMITS.metadataBytes);
    requireBytes(metadataLength);
    const metadata = JSON.parse(utf8.decode(new Uint8Array(buffer, offset, metadataLength)));
    offset += metadataLength;
    validateMetadata(metadata);
    const cellCount = metadata.columns * metadata.rows;
    const changedCount = integer(u32(), "changed cell count", 0, cellCount);
    if (changedCount > Math.floor((view.byteLength - offset) / 22))
        throw new Error("Truncated cell records");
    if (metadata.full && changedCount !== cellCount)
        throw new Error("Incomplete full frame");
    const cells = [];
    const seen = new Set();
    for (let i = 0; i < changedCount; i++) {
        requireBytes(22);
        const index = u32();
        if (index >= cellCount || seen.has(index))
            throw new Error("Invalid or duplicate cell index");
        seen.add(index);
        const foreground = u32();
        const background = u32();
        const underlineColor = u32();
        const attributes = view.getUint16(offset, true);
        const width = view.getUint8(offset + 2);
        const underlineStyle = view.getUint8(offset + 3);
        const textLength = view.getUint16(offset + 4, true);
        offset += 6;
        if (underlineStyle > 5)
            throw new Error("Unsupported underline style");
        requireBytes(textLength);
        const text = utf8.decode(new Uint8Array(buffer, offset, textLength));
        offset += textLength;
        cells.push({ index, foreground, background, underlineColor, attributes, width, underlineStyle, text });
    }
    const imageBytes = metadata.images.reduce((total, image) => total + image.byteLength, 0);
    if (imageBytes !== view.byteLength - offset)
        throw new Error("Image payload length mismatch");
    const images = metadata.images.map(image => {
        const bytes = new Uint8Array(buffer, offset, image.byteLength);
        offset += image.byteLength;
        return { ...image, bytes };
    });
    return { metadata, cells, images };
}
/** Mirror text, not ANSI; continuation cells are already represented by their lead glyph. */
export function screenText(cells, columns, rows) {
    const lines = [];
    for (let y = 0; y < rows; y++) {
        let line = "";
        for (let x = 0; x < columns; x++) {
            const cell = cells[y * columns + x];
            if (!cell || cell.width === 0)
                continue;
            line += cell.attributes & 64 ? " ".repeat(cell.width) : (cell.text || " ");
        }
        lines.push(line.replace(/ +$/u, ""));
    }
    return lines.join("\n");
}
//# sourceMappingURL=protocol.js.map