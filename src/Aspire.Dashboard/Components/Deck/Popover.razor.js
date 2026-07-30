// Deck popover positioning/dismissal. Positions a fixed-layer popover under (or above) an anchor
// element and dismisses it on outside click or Escape. Ancestor scroll/resize reposition the popover
// so it follows its anchor rather than dismissing. Colocated module: no inline script, so it's
// CSP-safe.

// Keyed by anchor id so multiple popovers can coexist and be disposed independently.
const registrations = new Map();

export function initialize(popoverElement, anchorId, dotNetHelper) {
    const anchor = document.getElementById(anchorId);
    if (!anchor) {
        return;
    }

    const reposition = () => position(popoverElement, anchor);
    reposition();

    // Scroll events don't bubble, but a capturing window listener still receives them for descendant
    // scrolls - including scrolling the popover's OWN scrollable body (e.g. a long checkbox list; see
    // .popover__body's overflow-y). Those internal scrolls don't move the anchor, so ignore them:
    // repositioning is only needed when an ancestor/page scroll shifts the anchor under the
    // fixed-position popover. The popover is never dismissed on scroll - it stays open and follows
    // its anchor.
    const onScroll = (e) => {
        if (e.target instanceof Node && popoverElement.contains(e.target)) {
            return;
        }
        reposition();
    };

    const onDocumentPointerDown = (e) => {
        if (!popoverElement.contains(e.target) && !anchor.contains(e.target)) {
            dotNetHelper.invokeMethodAsync("CloseFromJs");
        }
    };

    const onKeyDown = (e) => {
        if (e.key === "Escape") {
            dotNetHelper.invokeMethodAsync("CloseFromJs");
        }
    };

    // Defer attaching the outside-click handler so the same click that opened the popover
    // doesn't immediately close it.
    const attachTimer = setTimeout(() => {
        document.addEventListener("pointerdown", onDocumentPointerDown, true);
    }, 0);

    document.addEventListener("keydown", onKeyDown, true);
    // Capture scroll on any ancestor (useCapture) so nested scroll containers reposition too.
    window.addEventListener("scroll", onScroll, true);
    window.addEventListener("resize", reposition, true);

    registrations.set(anchorId, () => {
        clearTimeout(attachTimer);
        document.removeEventListener("pointerdown", onDocumentPointerDown, true);
        document.removeEventListener("keydown", onKeyDown, true);
        window.removeEventListener("scroll", onScroll, true);
        window.removeEventListener("resize", reposition, true);
    });
}

export function dispose(anchorId) {
    const cleanup = registrations.get(anchorId);
    if (cleanup) {
        cleanup();
        registrations.delete(anchorId);
    }
}

function position(popoverElement, anchor) {
    const anchorRect = anchor.getBoundingClientRect();
    const popoverRect = popoverElement.getBoundingClientRect();
    const margin = 4;
    const viewportHeight = window.innerHeight;
    const viewportWidth = window.innerWidth;

    // Prefer opening below the anchor; flip above when there isn't enough room.
    const spaceBelow = viewportHeight - anchorRect.bottom;
    const openAbove = spaceBelow < popoverRect.height + margin && anchorRect.top > spaceBelow;

    let top = openAbove
        ? anchorRect.top - popoverRect.height - margin
        : anchorRect.bottom + margin;

    // Right-align to the anchor, then clamp within the viewport.
    let left = anchorRect.right - popoverRect.width;
    left = Math.max(margin, Math.min(left, viewportWidth - popoverRect.width - margin));
    top = Math.max(margin, Math.min(top, viewportHeight - popoverRect.height - margin));

    popoverElement.style.top = `${Math.round(top)}px`;
    popoverElement.style.left = `${Math.round(left)}px`;
}
