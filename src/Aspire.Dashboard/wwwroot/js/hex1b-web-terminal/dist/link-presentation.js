const TEXT_ATTRIBUTES = 64 | 1024; // Hidden and soft-wrap; other SGR attributes are presentation-only.
function sameContent(cells, metadata, previousCells, previous) {
    if (cells === previousCells && metadata === previous)
        return true;
    if (metadata.columns !== previous.columns || metadata.rows !== previous.rows)
        return false;
    const history = metadata.history, oldHistory = previous.history;
    if (!!history !== !!oldHistory || history?.generation !== oldHistory?.generation ||
        history?.buffer !== oldHistory?.buffer || history?.rowIds.length !== oldHistory?.rowIds.length ||
        history?.rowIds.some((id, index) => id !== oldHistory?.rowIds[index]))
        return false;
    if (metadata.hyperlinks.length !== previous.hyperlinks.length ||
        metadata.hyperlinks.some((range, index) => {
            const old = previous.hyperlinks[index];
            return range.row !== old.row || range.startColumn !== old.startColumn ||
                range.endColumn !== old.endColumn || range.uri !== old.uri;
        }))
        return false;
    if (cells.length !== previousCells.length)
        return false;
    for (let index = 0; index < cells.length; index++) {
        const cell = cells[index], old = previousCells[index];
        if (cell === old)
            continue;
        if (!cell || !old || cell.text !== old.text || cell.width !== old.width ||
            (cell.attributes & TEXT_ATTRIBUTES) !== (old.attributes & TEXT_ATTRIBUTES))
            return false;
    }
    return true;
}
/** Tracks only local presentation state; never modifies authoritative cells or runs matching. */
export class LinkPresentation {
    enabled = false;
    generation = 0;
    presented;
    sent;
    forceSnapshot = false;
    decorations;
    pending;
    lastSerial = -1;
    configure(enabled, generation) {
        if (typeof enabled !== "boolean" || !Number.isSafeInteger(generation) || generation < 0) {
            throw new Error("Invalid link detection configuration");
        }
        if (generation < this.generation || (enabled === this.enabled && generation === this.generation))
            return false;
        this.enabled = enabled;
        this.generation = generation;
        this.sent = undefined;
        this.forceSnapshot = enabled;
        this.clear();
        this.lastSerial = -1;
        return true;
    }
    clear() {
        this.decorations = undefined;
        this.pending = undefined;
    }
    prepare(cells, metadata) {
        if (!this.enabled)
            return;
        if (!this.presented || !sameContent(cells, metadata, this.presented.cells, this.presented.metadata))
            this.clear();
        // A pending acknowledgement belongs to its submitted revision, even if text is unchanged.
        if (this.pending?.revision !== metadata.revision)
            this.pending = undefined;
    }
    present(cells, metadata) {
        this.presented = { cells, metadata };
        if (!this.enabled)
            return {};
        const snapshot = this.snapshot();
        return { linkGeneration: this.generation, ...(snapshot ? { linkSnapshot: snapshot } : {}) };
    }
    snapshot() {
        const current = this.presented;
        if (!this.enabled || !current)
            return undefined;
        if (!this.forceSnapshot && this.sent &&
            sameContent(current.cells, current.metadata, this.sent.cells, this.sent.metadata)) {
            this.sent = current;
            return undefined;
        }
        this.forceSnapshot = false;
        this.sent = current;
        const { cells, metadata } = current;
        return { revision: metadata.revision, columns: metadata.columns, rows: metadata.rows,
            cells, hyperlinks: metadata.hyperlinks };
    }
    accept(revision, generation, serial, ranges, busy, underlineStyle = "solid") {
        if (![revision, generation, serial].every(value => Number.isSafeInteger(value) && value >= 0) ||
            !Array.isArray(ranges) || !["solid", "dashed"].includes(underlineStyle)) {
            throw new Error("Invalid link decorations");
        }
        const metadata = this.presented?.metadata;
        if (!this.enabled || busy || !metadata || revision !== metadata.revision ||
            generation !== this.generation || serial <= this.lastSerial)
            return false;
        if (ranges.length > metadata.columns * metadata.rows || ranges.some(range => !range ||
            !Number.isInteger(range.row) || range.row < 0 || range.row >= metadata.rows ||
            !Number.isInteger(range.startColumn) || !Number.isInteger(range.endColumn) ||
            range.startColumn < 0 || range.endColumn <= range.startColumn || range.endColumn > metadata.columns)) {
            throw new Error("Invalid link decoration ranges");
        }
        const mask = new Uint8Array(metadata.columns * metadata.rows);
        // Values share the renderer's SGR underline styles, without modifying any cell.
        const style = underlineStyle === "dashed" ? 5 : 1;
        for (const range of ranges)
            mask.fill(style, range.row * metadata.columns + range.startColumn, range.row * metadata.columns + range.endColumn);
        this.lastSerial = serial;
        this.decorations = this.pending = { revision, generation, serial, mask };
        return true;
    }
    submission() {
        return { mask: this.decorations?.mask, acknowledgement: this.pending };
    }
    acknowledge(submitted, busy) {
        if (!submitted || submitted !== this.pending || busy || !this.enabled ||
            submitted.generation !== this.generation || submitted.revision !== this.presented?.metadata.revision)
            return undefined;
        this.pending = undefined;
        return { revision: submitted.revision, generation: submitted.generation, serial: submitted.serial };
    }
}
//# sourceMappingURL=link-presentation.js.map