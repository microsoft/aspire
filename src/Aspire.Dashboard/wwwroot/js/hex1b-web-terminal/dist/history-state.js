const unavailable = "Text history is not available for this terminal view";
const expired = "Selection expired: its text was changed, evicted, reset, or resized. Select the text again.";
const changedBeforeCopy = "Selection changed before copy completed. Copy the new selection again.";
/** Commands contain producer identities; text and ranges are never inferred from painted cells. */
export class HistoryState {
    #send;
    #change;
    #history = null;
    #revision = 0;
    #nextRequest = 0;
    #selectionRequest = 0;
    #viewportRequest = 0;
    #pendingMode = "character";
    #pendingAction = "clear";
    #pendingCopy;
    #deferredEndpoint;
    constructor(send, change = () => { }) {
        this.#send = send;
        this.#change = change;
    }
    get viewport() {
        if (!this.#history)
            return { available: false, following: true, pending: false };
        const { selection, copy, ...viewport } = this.#history;
        return { ...viewport, rowIds: [...viewport.rowIds], available: true, revision: this.#revision,
            pending: viewport.requestId < this.#viewportRequest };
    }
    get selection() {
        if (!this.#history)
            return { status: "unavailable", mode: "character", ranges: [], text: null, message: unavailable };
        const selection = this.#history.selection;
        if (selection.requestId < this.#selectionRequest || this.#deferredEndpoint) {
            return { requestId: this.#selectionRequest, status: "pending", mode: this.#pendingMode,
                canExtend: this.#pendingAction !== "clear",
                ranges: this.#pendingAction === "clear" ? [] : selection.ranges.map(range => ({ ...range })), text: null,
                message: "Resolving selection…" };
        }
        return { ...selection, canExtend: selection.status === "valid",
            ranges: selection.ranges.map(range => ({ ...range })), revision: this.#revision,
            message: selection.status === "invalidated" ? expired : "" };
    }
    accept(history, revision) {
        if (revision <= this.#revision)
            return false;
        const previousGeneration = this.#history?.generation;
        this.#history = history;
        this.#revision = revision;
        if (!history || (previousGeneration && previousGeneration !== history.generation) ||
            history.selection.status === "invalidated") {
            this.endGesture(true);
            this.#rejectCopy(new Error(expired));
        }
        const pending = this.#pendingCopy;
        if (pending && history?.copy?.requestId === pending.requestId) {
            if (history.copy.status !== "valid" || history.selection.status !== "valid" ||
                history.selection.requestId !== pending.selectionRequestId ||
                history.generation !== pending.generation || this.#selectionRequest > pending.selectionRequestId ||
                history.copy.text !== history.selection.text) {
                this.#rejectCopy(new Error(expired));
            }
            else {
                clearTimeout(pending.timer);
                this.#pendingCopy = undefined;
                pending.resolve(history.copy.text);
            }
        }
        if (this.#deferredEndpoint && !this.viewport.pending) {
            const point = this.#deferredEndpoint;
            this.#deferredEndpoint = undefined;
            this.extend(point);
        }
        this.#change();
        return true;
    }
    #requireHistory() {
        if (!this.#history)
            throw new Error(unavailable);
        return this.#history;
    }
    #pointRowId(point) {
        const history = this.#requireHistory();
        if (!point || !Number.isInteger(point.x) || point.x < 0 || point.x > 1023 ||
            !Number.isInteger(point.y) || point.y < 0 ||
            typeof history.rowIds[point.y] !== "string" || !history.rowIds[point.y].length) {
            throw new Error("Terminal viewport is not ready for that selection point. Wait for the next frame and try again.");
        }
        return history.rowIds[point.y];
    }
    #selectionChanged(mode = this.selection.mode, action = "extend") {
        this.#rejectCopy(new Error(changedBeforeCopy));
        this.#pendingMode = mode;
        this.#pendingAction = action;
        this.#selectionRequest = ++this.#nextRequest;
        return this.#selectionRequest;
    }
    begin(point, { mode, extend = false }) {
        const rowId = this.#pointRowId(point);
        this.#deferredEndpoint = undefined;
        const requestId = this.#selectionChanged(mode, extend ? "extend" : "start");
        this.#send({ type: "selection", action: extend ? "extend" : "start", mode, requestId,
            generation: this.#requireHistory().generation, rowId, column: point.x });
        this.#change();
    }
    extend(point) {
        this.#pointRowId(point);
        if (this.viewport.pending) {
            this.#rejectCopy(new Error(changedBeforeCopy));
            this.#pendingMode = this.selection.mode;
            this.#pendingAction = "extend";
            this.#deferredEndpoint = { ...point };
            this.#change();
            return;
        }
        this.begin(point, { mode: this.selection.mode, extend: true });
    }
    scroll(delta, endpoint) {
        this.#requireHistory();
        if (!Number.isSafeInteger(delta) || delta < -2147483648 || delta > 2147483647)
            throw new RangeError("Scroll delta must be a signed 32-bit integer");
        if (endpoint)
            this.#pointRowId(endpoint);
        if (!delta && !endpoint)
            return;
        if (endpoint)
            this.#deferredEndpoint = undefined;
        else
            this.#flushDeferredEndpoint();
        const requestId = endpoint ? this.#selectionChanged() : ++this.#nextRequest;
        this.#viewportRequest = requestId;
        this.#send({ type: "viewport", requestId, delta,
            ...(endpoint ? { extend: { row: endpoint.y, column: endpoint.x } } : {}) });
        this.#change();
    }
    live() {
        this.#requireHistory();
        this.#flushDeferredEndpoint();
        this.#viewportRequest = ++this.#nextRequest;
        this.#send({ type: "viewport", live: true, requestId: this.#viewportRequest });
        this.#change();
    }
    clear() {
        this.#requireHistory();
        this.#deferredEndpoint = undefined;
        const requestId = this.#selectionChanged(this.selection.mode, "clear");
        this.#send({ type: "selection", action: "clear", requestId });
        this.#change();
    }
    endGesture(cancelled = false) {
        if (cancelled && this.#deferredEndpoint) {
            this.#deferredEndpoint = undefined;
            this.#change();
        }
    }
    #flushDeferredEndpoint() {
        if (!this.#deferredEndpoint)
            return;
        const endpoint = this.#deferredEndpoint;
        this.#deferredEndpoint = undefined;
        // Preserve command order if another scroll overtakes the pending presentation.
        this.scroll(0, endpoint);
    }
    cancelCopy(error) { this.#rejectCopy(error); }
    copy() {
        const history = this.#requireHistory();
        const selection = this.selection;
        if (selection.status !== "valid")
            throw new Error(selection.message || "Select text before copying.");
        this.#rejectCopy(new Error("A newer copy request replaced this request."));
        const requestId = ++this.#nextRequest;
        const { promise, resolve, reject } = Promise.withResolvers();
        const timer = setTimeout(() => this.#rejectCopy(new Error("Timed out resolving selection. Copy again.")), 10000);
        this.#pendingCopy = { requestId, selectionRequestId: selection.requestId, generation: history.generation,
            resolve, reject, timer };
        try {
            this.#send({ type: "copy", requestId, selectionRequestId: selection.requestId, generation: history.generation });
        }
        catch (error) {
            this.#rejectCopy(error);
        }
        return promise;
    }
    #rejectCopy(error) {
        if (!this.#pendingCopy)
            return;
        clearTimeout(this.#pendingCopy.timer);
        this.#pendingCopy.reject(error);
        this.#pendingCopy = undefined;
    }
    disconnect() {
        this.#rejectCopy(new Error("Terminal view disconnected before copy completed."));
        this.#deferredEndpoint = undefined;
    }
}
//# sourceMappingURL=history-state.js.map