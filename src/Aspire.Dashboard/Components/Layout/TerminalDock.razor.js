// Drag-to-resize for the terminal dock's top edge.
//
// The dock is bottom-anchored (position: fixed; bottom: 0), so a taller dock means a *smaller* Y coordinate for its
// top edge. Height is therefore derived from the pointer's distance to the bottom of the viewport rather than from a
// delta, which keeps the grabber under the cursor even if a frame is dropped.
//
// Pointer capture is used so the drag survives the pointer leaving the 6px grabber, which is otherwise trivially easy
// at normal mouse speeds.

export function registerResizeHandle(dockElement, dotNetRef) {
    const grabber = dockElement.querySelector('.terminal-dock-resize-handle');
    if (!grabber) {
        return;
    }

    let dragging = false;

    grabber.addEventListener('pointerdown', (e) => {
        dragging = true;
        grabber.setPointerCapture(e.pointerId);
        e.preventDefault();
    });

    grabber.addEventListener('pointermove', (e) => {
        if (!dragging) {
            return;
        }

        const height = Math.round(window.innerHeight - e.clientY);
        dotNetRef.invokeMethodAsync('SetHeightAsync', height);
    });

    const end = (e) => {
        if (!dragging) {
            return;
        }
        dragging = false;
        try {
            grabber.releasePointerCapture(e.pointerId);
        } catch {
            // The pointer may already have been released by the browser (e.g. the tab lost focus mid-drag).
        }
    };

    grabber.addEventListener('pointerup', end);
    grabber.addEventListener('pointercancel', end);
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
