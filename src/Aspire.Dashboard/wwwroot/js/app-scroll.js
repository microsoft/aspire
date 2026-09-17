// Scroll-to-bottom button for live-data scroll containers.
// Console logs, traces, and structured logs can grow to thousands of lines. Add a floating jump-to-
// bottom button only when those regions meaningfully overflow and the user isn't already near the end.
//
// Design notes:
// - The control is appended to <body> and positioned with `position: fixed`, tracking the target's
//   getBoundingClientRect(). We deliberately do NOT wrap or inject nodes inside the scroll container
//   because that DOM is owned by Blazor's renderer; adding foreign children there can trip Blazor's
//   node diffing. A body-level sibling is invisible to the render tree.
// - A hidden <aspire-scroll-to-bottom> child registers its parent scroll container when connected
//   and cleans up when disconnected. No server interop or DOM mutation observers are needed.
// - Reposition/visibility updates are throttled through requestAnimationFrame and driven by the
//   container's own 'scroll', a ResizeObserver on the container and its scrolling content, and
//   window scroll/resize (capture-phase, because inner scroll events don't bubble to window).
// - Container scrolling only updates visibility. Layout and cached button dimensions are refreshed
//   on resize; ancestor scrolling also invalidates the container's position.
// - Hidden controls defer dirty layout until the reveal delay expires. Visible controls refresh
//   dirty layout on the next animation frame so they keep tracking the container.

// Only surface the buttons once there's a meaningful amount to scroll past, so they stay out of
// the way for small content. Roughly 1.5 viewports of the region reads as "large" in practice.
const overflowThreshold = 240;
// How far from an edge the user must be before the matching button appears.
const edgeThreshold = 120;
// Avoid flashing the button while a newly loaded page is still restoring its scroll position.
const buttonShowDelay = 200;
const scrollToBottomTagName = "aspire-scroll-to-bottom";

const chevronDown = '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4.47 7.03a.75.75 0 0 1 1.06-1.06L10 10.44l4.47-4.47a.75.75 0 1 1 1.06 1.06l-5 5a.75.75 0 0 1-1.06 0l-5-5Z"/></svg>';

let activeControl = null;
let animationFrameId = null;

function scheduleUpdate() {
    if (animationFrameId !== null || activeControl === null) {
        return;
    }
    animationFrameId = requestAnimationFrame(function () {
        animationFrameId = null;
        if (activeControl !== null) {
            updateEntry(activeControl);
        }
    });
}

function cancelPendingScrollToBottom(entry) {
    if (entry.scrollEndHandler !== null) {
        entry.container.removeEventListener("scrollend", entry.scrollEndHandler);
        entry.scrollEndHandler = null;
    }
}

function clearButtonShowTimer(entry) {
    if (entry.showTimer !== null) {
        clearTimeout(entry.showTimer);
        entry.showTimer = null;
    }
}

function hideBottomButton(entry, immediately = false) {
    clearButtonShowTimer(entry);
    entry.bottomBtn.classList.remove("is-visible");
    if (immediately) {
        // Bypass the normal opacity transition when the user has activated the button.
        entry.bottomBtn.hidden = true;
    }
}

function showBottomButton(entry) {
    clearButtonShowTimer(entry);
    entry.bottomBtn.hidden = false;
    entry.bottomBtn.classList.add("is-visible");
}

function scheduleBottomButtonShow(entry) {
    if (entry.showTimer !== null || entry.bottomBtn.classList.contains("is-visible")) {
        return;
    }

    entry.showTimer = setTimeout(function () {
        entry.showTimer = null;
        // Re-evaluate after the delay because page loading may have scrolled the region to the
        // bottom or removed it while the button was waiting to appear.
        updateEntry(entry, true);
    }, buttonShowDelay);
}

function scrollToLatestBottom(entry) {
    const container = entry.container;
    cancelPendingScrollToBottom(entry);

    const initialScrollHeight = container.scrollHeight;
    if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
        container.scrollTo({ top: initialScrollHeight, behavior: "auto" });
        return;
    }

    entry.scrollEndHandler = function () {
        entry.scrollEndHandler = null;
        // Native smooth scrolling retains its original destination. If streaming content moved
        // the bottom during the animation, finish with an immediate jump to the latest bottom.
        if (container.scrollHeight > initialScrollHeight) {
            container.scrollTop = container.scrollHeight;
        }
        scheduleUpdate();
    };
    container.addEventListener("scrollend", entry.scrollEndHandler, { once: true });

    container.scrollTo({ top: initialScrollHeight, behavior: "smooth" });
}

function makeButton(kind, label, svg) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "scroll-button scroll-to-" + kind;
    btn.setAttribute("aria-label", label);
    btn.setAttribute("title", label);
    // Supplemental affordance only - keyboard users can already scroll the focused region
    // natively, so keep these out of the tab order to avoid extra tab stops per container.
    btn.tabIndex = -1;
    btn.innerHTML = svg;
    return btn;
}

function initialize(container, label) {
    if (activeControl !== null) {
        unregister(activeControl);
    }

    const root = document.createElement("div");
    root.className = "scroll-buttons";
    const bottomBtn = makeButton("bottom", label, chevronDown);
    root.appendChild(bottomBtn);
    document.body.appendChild(root);

    const eventController = new AbortController();
    const entry = {
        container, root, bottomBtn, scrollEndHandler: null, showTimer: null,
        layoutDirty: true, layoutActive: false, buttonSize: null, eventController
    };
    activeControl = entry;

    bottomBtn.addEventListener("click", function () {
        hideBottomButton(entry, true);
        scrollToLatestBottom(entry);
    }, { signal: eventController.signal });

    const cancelForUserInput = function () {
        cancelPendingScrollToBottom(entry);
        scheduleUpdate();
    };
    container.addEventListener("wheel", cancelForUserInput, { passive: true, signal: eventController.signal });
    container.addEventListener("pointerdown", cancelForUserInput, { passive: true, signal: eventController.signal });
    container.addEventListener("keydown", cancelForUserInput, { signal: eventController.signal });

    window.addEventListener("scroll", onWindowScroll, { passive: true, capture: true, signal: eventController.signal });
    window.addEventListener("resize", onWindowResize, { passive: true, signal: eventController.signal });

    container.addEventListener("scroll", scheduleUpdate, { passive: true, signal: eventController.signal });
    const resizeObserver = new ResizeObserver(function (entries) {
        if (entries.some(resizeEntry => resizeEntry.target === container)) {
            entry.buttonSize = null;
            entry.layoutDirty = true;
        }
        scheduleUpdate();
    });
    resizeObserver.observe(container);
    // A fixed scrollport doesn't resize when filtering changes its scrollHeight, but its direct
    // content does. Defer observation because the registration element can connect before Blazor
    // inserts its stable following content sibling during the same render.
    queueMicrotask(function () {
        if (activeControl === entry) {
            for (const child of container.children) {
                if (child.localName !== scrollToBottomTagName) {
                    resizeObserver.observe(child);
                }
            }
        }
    });
    entry.resizeObserver = resizeObserver;

    scheduleUpdate();
    return entry;
}

function unregister(entry) {
    // A previous page can disconnect after the next page has already connected.
    if (activeControl !== entry) {
        return;
    }

    cancelPendingScrollToBottom(entry);
    clearButtonShowTimer(entry);
    entry.eventController.abort();
    entry.resizeObserver.disconnect();
    entry.root.remove();
    activeControl = null;
    if (animationFrameId !== null) {
        cancelAnimationFrame(animationFrameId);
        animationFrameId = null;
    }
}

// Separate scroll eligibility from geometry: hidden buttons wait through the reveal delay,
// while visible buttons keep tracking their container. The timer passes showImmediately=true
// to bypass only that delay; eligibility and any dirty layout are still checked again.
function updateEntry(entry, showImmediately = false) {
    const container = entry.container;

    if (!container.isConnected) {
        unregister(entry);
        return;
    }

    // Decide whether a jump is useful before measuring the container and clipping ancestors.
    // A pending smooth scroll owns the trip to the bottom, so keep the button hidden until it ends.
    const overflow = container.scrollHeight - container.clientHeight;
    const atBottom = overflow - container.scrollTop <= edgeThreshold;
    const shouldShow = overflow > overflowThreshold && !atBottom && entry.scrollEndHandler === null;
    if (!shouldShow) {
        // Cancel pending reveals without clearing layoutDirty. If the button is needed later,
        // its next reveal must account for geometry changes made while it was hidden.
        hideBottomButton(entry);
        if (overflow <= overflowThreshold) {
            entry.root.classList.remove("is-active");
        }
        return;
    }

    // Delay a hidden button's first measurement as well as its appearance. Initial scroll
    // restoration can cancel the reveal without paying for layout that was never displayed.
    if (!showImmediately && !entry.bottomBtn.classList.contains("is-visible")) {
        // A clean inactive layout cannot fit the button. Wait for geometry to change
        // before scheduling another reveal, without measuring a hidden control.
        if (entry.layoutDirty || entry.layoutActive) {
            scheduleBottomButtonShow(entry);
        }
        return;
    }

    // Only a visible button or an expired reveal timer reaches this point. Refresh geometry
    // before showing it, or hide it if ancestor clipping no longer leaves enough room.
    if (entry.layoutDirty) {
        updateLayout(entry);
    }
    entry.root.classList.toggle("is-active", entry.layoutActive);
    if (entry.layoutActive) {
        showBottomButton(entry);
    } else {
        hideBottomButton(entry);
    }
}

function updateLayout(entry) {
    entry.layoutDirty = false;
    const container = entry.container;
    const root = entry.root;
    const rect = container.getBoundingClientRect();
    const padding = 12;
    const scrollbarWidth = container.offsetWidth - container.clientWidth;
    let visibleLeft = Math.max(rect.left, 0);
    let visibleRight = Math.min(rect.right - scrollbarWidth, window.innerWidth);
    let visibleTop = Math.max(rect.top, 0);
    let visibleBottom = Math.min(rect.bottom, window.innerHeight);

    // The body-level control is not clipped with its container, so constrain it to the
    // intersection of ancestor scrollports as well as the viewport.
    for (let ancestor = container.parentElement; ancestor !== null; ancestor = ancestor.parentElement) {
        const style = getComputedStyle(ancestor);
        const clipsX = style.overflowX !== "visible";
        const clipsY = style.overflowY !== "visible";
        if (clipsX || clipsY) {
            const ancestorRect = ancestor.getBoundingClientRect();
            const left = ancestorRect.left + ancestor.clientLeft;
            const top = ancestorRect.top + ancestor.clientTop;
            if (clipsX) {
                visibleLeft = Math.max(visibleLeft, left);
                visibleRight = Math.min(visibleRight, left + ancestor.clientWidth);
            }
            if (clipsY) {
                visibleTop = Math.max(visibleTop, top);
                visibleBottom = Math.min(visibleBottom, top + ancestor.clientHeight);
            }
        }
    }

    const visibleWidth = Math.max(0, visibleRight - visibleLeft);
    const visibleHeight = Math.max(0, visibleBottom - visibleTop);
    if (entry.buttonSize === null) {
        const buttonStyle = getComputedStyle(entry.bottomBtn);
        entry.buttonSize = {
            width: Number.parseFloat(buttonStyle.width),
            height: Number.parseFloat(buttonStyle.height)
        };
    }
    const active =
        rect.width > 0 &&
        rect.height > 0 &&
        visibleWidth >= entry.buttonSize.width &&
        visibleHeight >= entry.buttonSize.height + padding * 2;

    entry.layoutActive = active;
    if (!active) {
        return;
    }

    // Center the control horizontally over the region and anchor it near the visible bottom edge.
    // Exclude the scrollbar from the horizontal center.
    root.style.right = "auto";
    root.style.bottom = "auto";
    root.style.left = (visibleLeft + visibleWidth / 2) + "px";
    root.style.top = (visibleTop + padding) + "px";
    root.style.height = (visibleHeight - padding * 2) + "px";
}

// Capture ancestor scrolls, which move the region relative to the viewport. The region's own
// scrolling changes only its content position and must not invalidate the cached layout.
function onWindowScroll(event) {
    const entry = activeControl;
    if (entry !== null && event.target !== entry.container &&
        (event.target === document || event.target === window || event.target.contains?.(entry.container))) {
        entry.layoutDirty = true;
        scheduleUpdate();
    }
}

function onWindowResize() {
    if (activeControl !== null) {
        activeControl.buttonSize = null;
        activeControl.layoutDirty = true;
        scheduleUpdate();
    }
}

class AspireScrollToBottom extends HTMLElement {
    #entry = null;

    connectedCallback() {
        if (this.isConnected && this.parentElement !== null && this.#entry === null) {
            this.#entry = initialize(this.parentElement, this.dataset.scrollToBottomLabel || "Scroll to bottom");
        }
    }

    disconnectedCallback() {
        if (this.#entry !== null) {
            unregister(this.#entry);
            this.#entry = null;
        }
    }
}

customElements.define(scrollToBottomTagName, AspireScrollToBottom);
