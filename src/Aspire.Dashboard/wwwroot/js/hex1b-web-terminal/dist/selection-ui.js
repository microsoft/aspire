import { isRecord } from "./validation.js";
const selectionFields = ["status", "mode", "requestId", "active", "pending", "canExtend",
    "text", "message", "copying", "copyError"];
const viewportFields = ["available", "following", "pending", "generation", "buffer",
    "top", "liveTop", "totalRows", "requestId"];
const geometryFields = ["columns", "rows", "cellWidth", "cellHeight", "mouseTracking"];
const rangeFields = ["row", "startColumn", "endColumn"];
const equalFields = (a, b, fields) => fields.every(field => a?.[field] === b?.[field]);
export function sameSelectionUIState(a, b) {
    return !!a && a.connected === b.connected && a.readOnly === b.readOnly &&
        equalFields(a.selection, b.selection, selectionFields) &&
        equalFields(a.viewport, b.viewport, viewportFields) &&
        equalFields(a.geometry, b.geometry, geometryFields) &&
        equalFields(a.canvasSize, b.canvasSize, ["width", "height"]) &&
        a.selection.ranges.length === b.selection.ranges.length &&
        a.selection.ranges.every((range, index) => equalFields(range, b.selection.ranges[index], rangeFields));
}
export function selectionRectangles(ranges, geometry, canvasSize) {
    const width = canvasSize.width / geometry.columns;
    const height = canvasSize.height / geometry.rows;
    return ranges.map(range => Object.freeze({
        left: range.startColumn * width, top: range.row * height,
        width: (range.endColumn - range.startColumn) * width, height
    }));
}
/** Owns UI notification/default rendering, not terminal selection or clipboard state. */
export class SelectionUI {
    #element;
    #overlay;
    #button;
    #signal;
    #getState;
    #runAction;
    #reportError;
    #previous;
    #notification;
    #queued = false;
    #force = false;
    constructor({ element, overlay, button, signal, getState, runAction, onSelectionUI, reportError }) {
        this.#element = element;
        this.#overlay = overlay;
        this.#button = button;
        this.#signal = signal;
        this.#getState = getState;
        this.#runAction = runAction;
        this.#reportError = reportError;
        if (onSelectionUI) {
            element.addEventListener("selectionui", event => {
                if (!this.#notification || event !== this.#notification)
                    return;
                const snapshot = this.#previous;
                try {
                    const result = onSelectionUI(this.#notification);
                    if (result !== undefined) {
                        if (isRecord(result) && typeof result.then === "function")
                            Promise.resolve(result).catch(error => {
                                if (!signal.aborted && this.#previous === snapshot)
                                    reportError(error);
                            });
                        throw new TypeError("onSelectionUI must finish synchronously; use preventDefault() and signal for UI ownership");
                    }
                }
                catch (error) {
                    event.preventDefault();
                    reportError(error);
                }
            }, { signal });
        }
    }
    refresh(force = false) {
        this.#force ||= force;
        if (this.#queued || this.#signal.aborted)
            return;
        this.#queued = true;
        // Coalesce geometry and history from the same presented frame before notifying the host.
        queueMicrotask(() => {
            this.#queued = false;
            if (this.#signal.aborted)
                return;
            const state = this.#getState();
            const force = this.#force;
            this.#force = false;
            if (!force && sameSelectionUIState(this.#previous, state))
                return;
            const snapshot = Object.freeze({
                ...state,
                selection: Object.freeze({ ...state.selection,
                    ranges: Object.freeze(state.selection.ranges.map(range => Object.freeze({ ...range }))) }),
                viewport: Object.freeze({ ...state.viewport, rowIds: Object.freeze([...(state.viewport.rowIds ?? [])]) }),
                geometry: Object.freeze({ ...state.geometry }),
                canvasSize: Object.freeze({ ...state.canvasSize })
            });
            this.#previous = snapshot;
            const detail = Object.freeze({
                ...snapshot, overlay: this.#overlay, signal: this.#signal, runAction: this.#runAction,
                rects: Object.freeze(selectionRectangles(snapshot.selection.ranges, snapshot.geometry, snapshot.canvasSize))
            });
            const event = new CustomEvent("selectionui", { cancelable: true, detail });
            this.#notification = event;
            this.#reportError(null);
            this.#element.dispatchEvent(event);
            if (this.#signal.aborted)
                return;
            const selection = snapshot.selection;
            this.#button.hidden = event.defaultPrevented || ["none", "unavailable"].includes(selection.status);
            this.#button.disabled = !snapshot.connected || selection.status !== "valid" || selection.copying;
            const label = selection.copying ? "Copying\u2026" : "Copy";
            if (this.#button.textContent !== label)
                this.#button.textContent = label;
        });
    }
}
//# sourceMappingURL=selection-ui.js.map