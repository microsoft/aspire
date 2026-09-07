// Pointer and keyboard resizing for the terminal dock's top edge.
//
// The dock is bottom-anchored (position: fixed; bottom: 0), so a taller dock means a *smaller* Y coordinate for its
// top edge. Height is therefore derived from the pointer's distance to the bottom of the viewport rather than from a
// delta, which keeps the grabber under the cursor even if a frame is dropped.
//
// Pointer capture is used so the drag survives the pointer leaving the 6px grabber, which is otherwise trivially easy
// at normal mouse speeds.

const resizeRegistrations = new WeakMap();

export function registerResizeHandle(dockElement, dotNetRef, minimumHeight, maximumHeight) {
    unregisterResizeHandle(dockElement);
    const grabber = dockElement.querySelector('.terminal-dock-resize-handle');
    if (!grabber) {
        throw new Error('The terminal dock resize handle was not found.');
    }

    let pointerId = null;
    let height = Math.round(dockElement.getBoundingClientRect().height);
    let viewportHeight = Math.max(1, window.innerHeight);
    let frame = null;
    let inFlight = false;
    let pending = false;
    let disposed = false;

    const bounds = () => {
        const max = Math.min(maximumHeight, viewportHeight);
        return { min: Math.min(minimumHeight, max), max };
    };

    // Coalesce a held key or pointer movement into at most one circuit call per frame, with only one call in
    // flight. Accumulate the requested height locally so delayed renders cannot lose repeated arrow-key steps.
    const scheduleUpdate = () => {
        pending = true;
        if (disposed || inFlight || frame !== null) {
            return;
        }
        frame = requestAnimationFrame(() => {
            frame = null;
            pending = false;
            inFlight = true;
            dotNetRef.invokeMethodAsync('SetHeightAsync', height, viewportHeight)
                .catch(error => {
                    if (!disposed) {
                        console.error('Failed to resize the terminal dock.', error);
                    }
                })
                .finally(() => {
                    inFlight = false;
                    if (pending && !disposed) {
                        scheduleUpdate();
                    }
                });
        });
    };

    const resizeTo = requestedHeight => {
        const { min, max } = bounds();
        const nextHeight = Math.max(min, Math.min(max, Math.round(requestedHeight)));
        if (height !== nextHeight) {
            height = nextHeight;
            scheduleUpdate();
        }
    };

    const onPointerDown = e => {
        if (e.button !== 0 || !e.isPrimary || dockElement.inert) {
            return;
        }
        pointerId = e.pointerId;
        grabber.setPointerCapture(e.pointerId);
        grabber.focus({ preventScroll: true });
        e.preventDefault();
    };

    const onPointerMove = e => {
        if (pointerId !== e.pointerId || dockElement.inert) {
            return;
        }
        resizeTo(viewportHeight - e.clientY);
    };

    const end = e => {
        if (pointerId !== e.pointerId) {
            return;
        }
        pointerId = null;
        if (grabber.hasPointerCapture(e.pointerId)) {
            grabber.releasePointerCapture(e.pointerId);
        }
    };

    // Follow the focused window-splitter pattern: https://www.w3.org/WAI/ARIA/apg/patterns/windowsplitter/.
    // The dock is the bottom pane, so moving the separator up increases its height. Shift adds coarse adjustment;
    // no modifier shortcut is registered on the dock or the terminal input itself.
    const onKeyDown = e => {
        if (dockElement.inert || e.target !== grabber || e.ctrlKey || e.altKey || e.metaKey || e.isComposing) {
            return;
        }
        const step = e.shiftKey ? 50 : 10;
        const { min, max } = bounds();
        let nextHeight;
        switch (e.key) {
            case 'ArrowUp':
                nextHeight = height + step;
                break;
            case 'ArrowDown':
                nextHeight = height - step;
                break;
            case 'Home':
                if (e.shiftKey) return;
                nextHeight = min;
                break;
            case 'End':
                if (e.shiftKey) return;
                nextHeight = max;
                break;
            default:
                return;
        }
        e.preventDefault();
        e.stopPropagation();
        resizeTo(nextHeight);
    };

    const onViewportResize = () => {
        viewportHeight = Math.max(1, window.innerHeight);
        resizeTo(height);
        // Bounds can change even if the current height still fits.
        scheduleUpdate();
    };

    grabber.addEventListener('pointerdown', onPointerDown);
    grabber.addEventListener('pointermove', onPointerMove);
    grabber.addEventListener('pointerup', end);
    grabber.addEventListener('pointercancel', end);
    grabber.addEventListener('lostpointercapture', end);
    grabber.addEventListener('keydown', onKeyDown);
    window.addEventListener('resize', onViewportResize);
    onViewportResize();

    resizeRegistrations.set(dockElement, () => {
        disposed = true;
        if (frame !== null) {
            cancelAnimationFrame(frame);
        }
        grabber.removeEventListener('pointerdown', onPointerDown);
        grabber.removeEventListener('pointermove', onPointerMove);
        grabber.removeEventListener('pointerup', end);
        grabber.removeEventListener('pointercancel', end);
        grabber.removeEventListener('lostpointercapture', end);
        grabber.removeEventListener('keydown', onKeyDown);
        window.removeEventListener('resize', onViewportResize);
        if (pointerId !== null && grabber.hasPointerCapture(pointerId)) {
            grabber.releasePointerCapture(pointerId);
        }
    });
}

export function unregisterResizeHandle(dockElement) {
    resizeRegistrations.get(dockElement)?.();
    resizeRegistrations.delete(dockElement);
}

const tabNavigationRegistrations = new WeakMap();

export function registerTabNavigation(dockElement) {
    unregisterTabNavigation(dockElement);
    let focusedTabGroup = null;

    const onFocusIn = (event) => {
        const group = event.target.closest?.('.terminal-dock-tab');
        focusedTabGroup = group && dockElement.contains(group) ? group : null;
    };

    // Automatic activation follows https://www.w3.org/WAI/ARIA/apg/patterns/tabs/.
    // Only tab headers handle these keys. Native buttons provide Enter/Space, while xterm, close buttons and
    // browser shortcuts keep their own input handling. Moving focus locally avoids waiting for a circuit round-trip.
    const onKeyDown = (event) => {
        const tab = event.target.closest?.('.terminal-dock-tab-select');
        if (!tab || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey || event.isComposing) {
            return;
        }

        const tabs = Array.from(dockElement.querySelectorAll('.terminal-dock-tab-select'));
        const index = tabs.indexOf(tab);
        let nextIndex;
        switch (event.key) {
            case 'ArrowLeft':
                nextIndex = (index + tabs.length - 1) % tabs.length;
                break;
            case 'ArrowRight':
                nextIndex = (index + 1) % tabs.length;
                break;
            case 'Home':
                nextIndex = 0;
                break;
            case 'End':
                nextIndex = tabs.length - 1;
                break;
            case 'Delete':
                event.preventDefault();
                event.stopPropagation();
                if (!event.repeat) {
                    tab.closest('.terminal-dock-tab').querySelector('.terminal-dock-tab-close').click();
                }
                return;
            default:
                return;
        }

        event.preventDefault();
        event.stopPropagation();
        tabs[nextIndex].focus({ preventScroll: true });
        tabs[nextIndex].scrollIntoView({ block: 'nearest', inline: 'nearest' });
        tabs[nextIndex].click();
    };

    // Removal is confirmed by the watch stream, not by the close RPC finishing. A focused node's removal leaves
    // focus on the document body, so remember the group until the DOM update arrives. Moving elsewhere while a
    // close is pending clears it; unrelated metadata updates must not steal focus from a terminal or the page.
    const observer = new MutationObserver(() => {
        if (!focusedTabGroup || focusedTabGroup.isConnected) {
            return;
        }

        focusedTabGroup = null;
        if (!dockElement.isConnected || dockElement.inert) {
            return;
        }

        const target = dockElement.querySelector('.terminal-dock-tab-select[aria-selected="true"]')
            || dockElement.querySelector('.terminal-dock-collapse');
        target.focus({ preventScroll: true });
        target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    });

    document.addEventListener('focusin', onFocusIn);
    dockElement.addEventListener('keydown', onKeyDown);
    onFocusIn({ target: document.activeElement });
    observer.observe(dockElement, { childList: true, subtree: true });
    tabNavigationRegistrations.set(dockElement, () => {
        document.removeEventListener('focusin', onFocusIn);
        dockElement.removeEventListener('keydown', onKeyDown);
        observer.disconnect();
    });
}

export function unregisterTabNavigation(dockElement) {
    tabNavigationRegistrations.get(dockElement)?.();
    tabNavigationRegistrations.delete(dockElement);
}
