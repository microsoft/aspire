// Deck data-grid column resizing. Attaches to a table (or a CSS-grid container) and lets the user
// resize columns by dragging the handle between two adjacent header cells, or with the keyboard when
// a handle is focused. Colocated module: no inline script, so it's CSP-safe. Every listener is
// removed in dispose().

// Keyed by an id supplied per initialized grid so multiple grids coexist and clean up independently.
const registrations = new Map();

const DEFAULT_MIN_WIDTH = 48;
const KEYBOARD_STEP = 16;

export function initialize(marker, options) {
    const root = marker?.previousElementSibling;
    if (!root) {
        return;
    }

    const id = options.id;
    const minWidth = options.minWidth || DEFAULT_MIN_WIDTH;
    const gridColumnsVar = options.gridColumnsVar || null;
    const isTable = root.tagName === "TABLE";

    const getHeaderRow = () =>
        isTable
            ? root.querySelector("thead tr")
            : root.querySelector("[data-resize-header]");

    const getHeaderCells = () => {
        const headerRow = getHeaderRow();
        if (!headerRow) {
            return [];
        }
        return Array.from(headerRow.children).filter(
            (el) => el.tagName === "TH" || el.classList.contains("column-header"));
    };

    // Apply an array of pixel widths to the underlying layout.
    const applyWidths = (widths) => {
        if (isTable) {
            const cols = Array.from(root.querySelectorAll("colgroup col"));
            for (let i = 0; i < cols.length && i < widths.length; i++) {
                cols[i].style.width = `${Math.round(widths[i])}px`;
            }
        } else if (gridColumnsVar) {
            root.style.setProperty(gridColumnsVar, widths.map((w) => `${Math.round(w)}px`).join(" "));
        }
    };

    const measureWidths = () => {
        if (!isTable) {
            // CSS-grid item bounds exclude the gap between tracks. Feeding those bounds back into
            // grid-template-columns would therefore shrink a track by the gap on every move event.
            // Browsers expose the resolved track list as pixel values, which is the authoritative
            // source for resizing the shared header/body template.
            const headerRow = getHeaderRow();
            if (headerRow) {
                const tracks = getComputedStyle(headerRow).gridTemplateColumns
                    .split(/\s+/)
                    .map((value) => Number.parseFloat(value));
                if (tracks.length > 0 && tracks.every(Number.isFinite)) {
                    return tracks;
                }
            }
        }

        return getHeaderCells().map((cell) => cell.getBoundingClientRect().width);
    };

    // Resize the pair (index, index+1) by dx pixels, clamping both to the minimum width so the total
    // width of the pair (and therefore the grid) stays constant.
    const resizePair = (index, dx) => {
        const widths = measureWidths();
        if (index < 0 || index + 1 >= widths.length) {
            return;
        }

        let delta = dx;
        if (widths[index] + delta < minWidth) {
            delta = minWidth - widths[index];
        }
        if (widths[index + 1] - delta < minWidth) {
            delta = widths[index + 1] - minWidth;
        }

        widths[index] += delta;
        widths[index + 1] -= delta;
        applyWidths(widths);

        return widths;
    };

    const updateHandleValues = (handle, index) => {
        const widths = measureWidths();
        if (index >= 0 && index < widths.length) {
            handle.setAttribute("aria-valuenow", String(Math.round(widths[index])));
            handle.setAttribute("aria-valuetext", `${Math.round(widths[index])}px`);
        }
    };

    // The set of resize handles changes at runtime: the responsive GridColumnManager adds and
    // removes columns (and therefore handles) as the viewport changes. Rather than binding listeners
    // to the handles that happen to exist at initialize() time (which would leave newly inserted
    // handles dead and leak references to removed ones), we delegate a single set of listeners on the
    // root and resolve the target handle per event. A MutationObserver keeps ARIA metadata current on
    // handles as they come and go.

    const handleIndex = (handle) => parseInt(handle.getAttribute("data-column-index"), 10);

    // Initialize (or refresh) the ARIA range metadata for a handle.
    const initHandleAria = (handle) => {
        handle.setAttribute("aria-valuemin", String(minWidth));
        updateHandleValues(handle, handleIndex(handle));
    };

    const getHandles = () => Array.from(root.querySelectorAll("[data-resize-handle]"));
    for (const handle of getHandles()) {
        initHandleAria(handle);
    }

    // Pointer drag, delegated from the root so it works for handles inserted after initialization.
    const onRootPointerDown = (e) => {
        const handle = e.target.closest("[data-resize-handle]");
        if (!handle || !root.contains(handle)) {
            return;
        }
        // Only respond to the primary button / touch / pen.
        if (e.button !== undefined && e.button !== 0) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();

        const index = handleIndex(handle);
        let previousX = e.clientX;
        try {
            handle.setPointerCapture(e.pointerId);
        } catch {
            // Pointer capture is best-effort.
        }

        const onPointerMove = (moveEvent) => {
            const delta = moveEvent.clientX - previousX;
            previousX = moveEvent.clientX;
            resizePair(index, delta);
            updateHandleValues(handle, index);
        };
        const onPointerUp = () => {
            handle.removeEventListener("pointermove", onPointerMove);
            handle.removeEventListener("pointerup", onPointerUp);
            handle.removeEventListener("pointercancel", onPointerUp);
            try {
                handle.releasePointerCapture(e.pointerId);
            } catch {
                // Ignore.
            }
        };

        handle.addEventListener("pointermove", onPointerMove);
        handle.addEventListener("pointerup", onPointerUp);
        handle.addEventListener("pointercancel", onPointerUp);
    };

    // Keyboard resizing when a handle is focused, delegated from the root.
    const onRootKeyDown = (e) => {
        const handle = e.target.closest("[data-resize-handle]");
        if (!handle || !root.contains(handle)) {
            return;
        }
        let dx = 0;
        if (e.key === "ArrowLeft") {
            dx = -KEYBOARD_STEP;
        } else if (e.key === "ArrowRight") {
            dx = KEYBOARD_STEP;
        } else {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        const index = handleIndex(handle);
        resizePair(index, dx);
        updateHandleValues(handle, index);
    };

    // Clicking a handle must not trigger the header's sort toggle.
    const onRootClick = (e) => {
        const handle = e.target.closest("[data-resize-handle]");
        if (handle && root.contains(handle)) {
            e.stopPropagation();
        }
    };

    root.addEventListener("pointerdown", onRootPointerDown);
    root.addEventListener("keydown", onRootKeyDown);
    root.addEventListener("click", onRootClick);

    // Keep ARIA metadata correct for handles inserted after initialization (responsive columns).
    const observer = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node.nodeType !== Node.ELEMENT_NODE) {
                    continue;
                }
                if (node.matches?.("[data-resize-handle]")) {
                    initHandleAria(node);
                }
                for (const handle of node.querySelectorAll?.("[data-resize-handle]") ?? []) {
                    initHandleAria(handle);
                }
            }
        }
    });
    observer.observe(root, { childList: true, subtree: true });

    registrations.set(id, () => {
        observer.disconnect();
        root.removeEventListener("pointerdown", onRootPointerDown);
        root.removeEventListener("keydown", onRootKeyDown);
        root.removeEventListener("click", onRootClick);
    });
}

export function dispose(id) {
    const cleanup = registrations.get(id);
    if (cleanup) {
        cleanup();
        registrations.delete(id);
    }
}
