import { LINK_LIMITS } from "./link-options.js";
const SOFT_WRAP = 1024;
const HIDDEN = 64;
const segmenter = new Intl.Segmenter(undefined, { granularity: "grapheme" });
export function extractLinkText(snapshot, mode) {
    const { columns, rows, cells } = snapshot;
    if (!Number.isSafeInteger(columns) || !Number.isSafeInteger(rows) || columns < 1 || rows < 1 ||
        columns * rows > LINK_LIMITS.cells)
        throw new RangeError("Link grid exceeds cell limit");
    const result = [];
    let text = "", spans = [], unsafe = new Set(), firstRow = 0, total = 0;
    let start = "unknown";
    const wrapped = (row) => row >= 0 && !!((cells[(row + 1) * columns - 1]?.attributes ?? 0) & SOFT_WRAP);
    const finish = (end) => {
        const chunk = Object.freeze({ text, mode, start, end });
        const boundaries = new Set([text.length]);
        for (const segment of segmenter.segment(text))
            boundaries.add(segment.index);
        result.push({ chunk, spans, unsafe, boundaries,
            key: JSON.stringify([mode, firstRow, start, end, text]) });
        text = "";
        spans = [];
        unsafe = new Set();
    };
    for (let row = 0; row < rows; row++) {
        if (text.length === 0) {
            firstRow = row;
            start = row === 0 ? "unknown" : wrapped(row - 1) ? "clipped" : "complete";
        }
        for (let col = 0; col < columns; col++) {
            const cell = cells[row * columns + col];
            const width = cell?.width ?? 1;
            const valid = width >= 1 && width <= 2 && col + width <= columns &&
                !(cell && ((cell.attributes & HIDDEN) || cell.text.codePointAt(0) === 0x10eeee)) &&
                (width !== 2 || cells[row * columns + col + 1]?.width === 0);
            const value = valid ? cell?.text || " " : "\0";
            if (total + value.length > LINK_LIMITS.totalText || text.length + value.length > LINK_LIMITS.chunk) {
                throw new RangeError("Link text exceeds chunk or viewport limit");
            }
            const offset = text.length;
            text += value;
            total += value.length;
            if (valid)
                spans.push({ start: offset, end: text.length, row, startColumn: col, endColumn: col + width });
            else {
                unsafe.add(offset);
                unsafe.add(text.length);
            }
            if (valid && width === 2)
                col++;
        }
        const soft = wrapped(row);
        if (!soft)
            unsafe.add(text.length); // HWT1 cannot distinguish a full hard row from a right crop.
        if (mode === "physicalRow" || (mode === "logicalLine" && !soft) || row === rows - 1) {
            finish(soft ? "clipped" : "unknown");
        }
        else if (!soft) {
            if (++total > LINK_LIMITS.totalText || text.length + 1 > LINK_LIMITS.chunk)
                throw new RangeError("Link text exceeds limit");
            text += "\n";
        }
    }
    return result;
}
export function mapLinkRange(mapped, index, length) {
    const end = index + length, { chunk } = mapped;
    if (length <= 0 || index < 0 || end > chunk.text.length || !mapped.boundaries.has(index) ||
        !mapped.boundaries.has(end) || (index === 0 && chunk.start !== "complete") ||
        (end === chunk.text.length && chunk.end !== "complete") ||
        chunk.text.slice(index, end).includes("\0"))
        return null;
    for (const boundary of mapped.unsafe) {
        if (boundary === index || boundary === end ||
            (boundary > index && boundary < end && chunk.text[boundary - 1] !== " "))
            return null;
    }
    const ranges = [];
    for (const span of mapped.spans) {
        if (span.end <= index || span.start >= end)
            continue;
        if (span.start < index || span.end > end)
            return null;
        const last = ranges.at(-1);
        if (last && last.row === span.row && last.endColumn === span.startColumn)
            last.endColumn = span.endColumn;
        else
            ranges.push({ row: span.row, startColumn: span.startColumn, endColumn: span.endColumn });
    }
    return ranges.length ? ranges : null;
}
//# sourceMappingURL=link-text.js.map