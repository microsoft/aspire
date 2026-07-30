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

    const cleanups = [];

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

    const handles = Array.from(root.querySelectorAll("[data-resize-handle]"));
    for (const handle of handles) {
        const index = parseInt(handle.getAttribute("data-column-index"), 10);
        handle.setAttribute("aria-valuemin", String(minWidth));
        updateHandleValues(handle, index);

        // Pointer drag.
        const onPointerDown = (e) => {
            // Only respond to the primary button / touch / pen.
            if (e.button !== undefined && e.button !== 0) {
                return;
            }
            e.preventDefault();
            e.stopPropagation();

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

        // Keyboard resizing when the handle is focused.
        const onKeyDown = (e) => {
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
            resizePair(index, dx);
            updateHandleValues(handle, index);
        };

        handle.addEventListener("pointerdown", onPointerDown);
        handle.addEventListener("keydown", onKeyDown);
        // Clicking the handle must not trigger the header's sort toggle.
        const onClick = (e) => e.stopPropagation();
        handle.addEventListener("click", onClick);

        cleanups.push(() => {
            handle.removeEventListener("pointerdown", onPointerDown);
            handle.removeEventListener("keydown", onKeyDown);
            handle.removeEventListener("click", onClick);
        });
    }

    registrations.set(id, () => {
        for (const cleanup of cleanups) {
            cleanup();
        }
    });
}

export function dispose(id) {
    const cleanup = registrations.get(id);
    if (cleanup) {
        cleanup();
        registrations.delete(id);
    }
}
