// Pointer and keyboard resizing for the terminal dock's top edge.
//
// The dock is bottom-anchored (position: fixed; bottom: 0), so a taller dock means a *smaller* Y coordinate for its
// top edge. Height is therefore derived from the pointer's distance to the bottom of the viewport rather than from a
// delta, which keeps the grabber under the cursor even if a frame is dropped.
//
// Pointer capture is used so the drag survives the pointer leaving the 6px grabber, which is otherwise trivially easy
// at normal mouse speeds.

const resizeRegistrations = new WeakMap();
const updateIntervalMs = 100;

export function registerResizeHandle(dockElement, dotNetRef, minimumHeight, maximumHeight) {
    unregisterResizeHandle(dockElement);
    const grabber = dockElement.querySelector('.terminal-dock-resize-handle');
    if (!grabber) {
        throw new Error('The terminal dock resize handle was not found.');
    }

    let pointerId = null;
    let height = Math.round(dockElement.getBoundingClientRect().height);
    let viewportHeight = Math.max(1, window.innerHeight);
    let dragChanged = false;
    let updateTimer = null;
    let preserveFocusOnUpdate = false;
    let handleWasFocused = document.activeElement === grabber;
    let disposed = false;

    const bounds = () => {
        const max = Math.min(maximumHeight, viewportHeight);
        return { min: Math.min(minimumHeight, max), max };
    };

    const updateServer = (preserveFocus = false) => {
        if (disposed) {
            return Promise.resolve();
        }
        return dotNetRef.invokeMethodAsync('SetHeightAsync', height, viewportHeight)
            .then(() => {
                if (preserveFocus && handleWasFocused && !disposed && (document.activeElement === document.body || document.activeElement === null)) {
                    grabber.focus({ preventScroll: true });
                }
            })
            .catch(error => {
                if (!disposed) {
                    console.error('Failed to resize the terminal dock.', error);
                }
            });
    };

    const scheduleUpdate = () => {
        if (updateTimer !== null) {
            return;
        }
        updateTimer = setTimeout(() => {
            updateTimer = null;
            dragChanged = false;
            updateServer(preserveFocusOnUpdate);
            preserveFocusOnUpdate = false;
        }, updateIntervalMs);
    };

    const resizeTo = requestedHeight => {
        const { min, max } = bounds();
        const nextHeight = Math.max(min, Math.min(max, Math.round(requestedHeight)));
        if (height !== nextHeight) {
            height = nextHeight;
            dockElement.style.height = `${height}px`;
            return true;
        }
        return false;
    };

    const onPointerDown = e => {
        if (e.button !== 0 || !e.isPrimary || dockElement.inert) {
            return;
        }
        pointerId = e.pointerId;
        dragChanged = false;
        grabber.setPointerCapture(e.pointerId);
        grabber.focus({ preventScroll: true });
        e.preventDefault();
    };

    const onPointerMove = e => {
        if (pointerId !== e.pointerId || dockElement.inert) {
            return;
        }
        dragChanged = resizeTo(viewportHeight - e.clientY) || dragChanged;
        if (dragChanged) {
            scheduleUpdate();
        }
    };

    const end = e => {
        if (pointerId !== e.pointerId) {
            return;
        }
        pointerId = null;
        if (grabber.hasPointerCapture(e.pointerId)) {
            grabber.releasePointerCapture(e.pointerId);
        }
        if (dragChanged) {
            if (updateTimer !== null) {
                clearTimeout(updateTimer);
                updateTimer = null;
            }
            dragChanged = false;
            updateServer(preserveFocusOnUpdate);
            preserveFocusOnUpdate = false;
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
        if (resizeTo(nextHeight)) {
            updateServer();
        }
    };

    const onViewportResize = () => {
        viewportHeight = Math.max(1, window.innerHeight);
        resizeTo(height);
        preserveFocusOnUpdate ||= handleWasFocused;
        // Bounds can change even if the current height still fits.
        scheduleUpdate();
    };

    const onDocumentFocusIn = e => {
        handleWasFocused = e.target === grabber;
    };

    grabber.addEventListener('pointerdown', onPointerDown);
    grabber.addEventListener('pointermove', onPointerMove);
    grabber.addEventListener('pointerup', end);
    grabber.addEventListener('pointercancel', end);
    grabber.addEventListener('lostpointercapture', end);
    grabber.addEventListener('keydown', onKeyDown);
    document.addEventListener('focusin', onDocumentFocusIn);
    window.addEventListener('resize', onViewportResize);
    resizeTo(height);
    updateServer();

    resizeRegistrations.set(dockElement, () => {
        disposed = true;
        if (updateTimer !== null) {
            clearTimeout(updateTimer);
            updateTimer = null;
        }
        grabber.removeEventListener('pointerdown', onPointerDown);
        grabber.removeEventListener('pointermove', onPointerMove);
        grabber.removeEventListener('pointerup', end);
        grabber.removeEventListener('pointercancel', end);
        grabber.removeEventListener('lostpointercapture', end);
        grabber.removeEventListener('keydown', onKeyDown);
        document.removeEventListener('focusin', onDocumentFocusIn);
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

export function registerTabNavigation(dockElement, dotNetRef) {
    unregisterTabNavigation(dockElement);
    let focusedTabGroup = null;
    const disposeTabStrip = registerTabStrip(dockElement, dotNetRef);

    const onFocusIn = (event) => {
        const group = event.target.closest?.('.terminal-dock-tab');
        focusedTabGroup = group && dockElement.contains(group) ? group : null;
    };

    // Automatic activation follows https://www.w3.org/WAI/ARIA/apg/patterns/tabs/.
    // Only tab headers handle these keys. Native buttons provide Enter/Space, while terminal input, close buttons and
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
        tabs[nextIndex].closest('.terminal-dock-tab').scrollIntoView({ block: 'nearest', inline: 'nearest' });
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
        (target.closest('.terminal-dock-tab') ?? target).scrollIntoView({ block: 'nearest', inline: 'nearest' });
    });

    document.addEventListener('focusin', onFocusIn);
    dockElement.addEventListener('keydown', onKeyDown);
    onFocusIn({ target: document.activeElement });
    observer.observe(dockElement, { childList: true, subtree: true });
    tabNavigationRegistrations.set(dockElement, () => {
        disposeTabStrip();
        document.removeEventListener('focusin', onFocusIn);
        dockElement.removeEventListener('keydown', onKeyDown);
        observer.disconnect();
    });
}

export function unregisterTabNavigation(dockElement) {
    tabNavigationRegistrations.get(dockElement)?.();
    tabNavigationRegistrations.delete(dockElement);
}

function registerTabStrip(dock, dotNetRef) {
    let list = null;
    let selected = null;
    let dragged = null;
    let dropTarget = null;
    let dropAfter = false;
    let dragX = null;
    let scrollTimer = null;
    let pending = false;
    let disposed = false;
    const listeners = new AbortController();
    const groups = () => Array.from(list?.querySelectorAll('.terminal-dock-tab') ?? []);
    const isRtl = () => getComputedStyle(list).direction === 'rtl';

    const updateOverflow = () => {
        const controls = dock.querySelector('.terminal-dock-tab-scroll');
        const tabs = groups();
        const viewport = list?.getBoundingClientRect();
        const boxes = tabs.map(tab => tab.getBoundingClientRect());
        for (const button of controls.querySelectorAll('[data-tab-scroll]')) {
            const canScroll = button.dataset.tabScroll === '-1'
                ? boxes.some(box => box.left < viewport.left - 1)
                : boxes.some(box => box.right > viewport.right + 1);
            button.toggleAttribute('disabled', !canScroll);
            button.setAttribute('aria-disabled', String(!canScroll));
            if (button.dataset.tabScroll === '1') {
                dock.querySelector('.terminal-dock-tabs')?.toggleAttribute('data-overflow-right', canScroll);
            }
        }
    };
    const resizeObserver = new ResizeObserver(updateOverflow);
    const refresh = () => {
        const current = dock.querySelector('.terminal-dock-tablist');
        if (current !== list) {
            resizeObserver.disconnect();
            list = current;
            if (list) {
                resizeObserver.observe(list);
                resizeObserver.observe(dock.querySelector('.terminal-dock-tabs'));
            }
        }
        const active = list?.querySelector('[aria-selected="true"]');
        if (active && active !== selected && !dock.inert) {
            active.closest('.terminal-dock-tab').scrollIntoView({ block: 'nearest', inline: 'nearest' });
        }
        selected = dock.inert ? null : active;
        if (dragged && (!dragged.isConnected || dock.inert)) clearDrag();
        updateOverflow();
    };
    const observer = new MutationObserver(refresh);

    const clearDrop = () => {
        dropTarget?.removeAttribute('data-drop-position');
        dropTarget = null;
    };
    const clearDrag = () => {
        clearDrop();
        dragged?.classList.remove('dragging');
        dragged = null;
        dragX = null;
        clearTimeout(scrollTimer);
        scrollTimer = null;
    };
    const updateDrop = x => {
        clearDrop();
        const candidates = groups().filter(group => group !== dragged);
        const rtl = isRtl();
        dropTarget = candidates.find(group => {
            const box = group.getBoundingClientRect();
            return rtl ? x > box.left + box.width / 2 : x < box.left + box.width / 2;
        }) ?? candidates.at(-1);
        if (dropTarget) {
            const box = dropTarget.getBoundingClientRect();
            dropAfter = rtl ? x < box.left + box.width / 2 : x > box.left + box.width / 2;
            dropTarget.dataset.dropPosition = dropAfter !== rtl ? 'after' : 'before';
        }
    };
    const reorder = async (source, target, after, restoreFocus) => {
        if (pending || disposed) return;
        pending = true;
        try {
            // Blazor owns node order and keyed terminal viewers; never move their DOM nodes directly.
            await dotNetRef.invokeMethodAsync('ReorderTerminalAsync', source.dataset.terminalId, target.dataset.terminalId, after);
            if (!disposed && !dock.inert && source.isConnected) {
                const tab = source.querySelector('.terminal-dock-tab-select');
                if (restoreFocus && (document.activeElement === document.body || source.contains(document.activeElement))) {
                    tab.focus({ preventScroll: true });
                }
                source.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            }
        } catch (error) {
            if (!disposed) console.error('Failed to reorder terminal dock tabs.', error);
        } finally {
            pending = false;
        }
    };
    const autoScroll = () => {
        scrollTimer = null;
        if (!dragged || dragX === null || !list || dock.inert) return;
        const box = list.getBoundingClientRect();
        const delta = dragX < box.left + 28 ? -12 : dragX > box.right - 28 ? 12 : 0;
        if (delta) {
            list.scrollBy({ left: delta, behavior: 'instant' });
            // The pointer can stay still while scrolling moves a different tab underneath it.
            updateDrop(dragX);
        }
        scrollTimer = setTimeout(autoScroll, 50);
    };

    dock.addEventListener('click', event => {
        const button = event.target.closest?.('[data-tab-scroll]');
        if (!button || !list || dock.inert || button.hasAttribute('disabled')) return;
        const distance = Math.max(80, list.clientWidth * 0.75);
        list.scrollBy({ left: Number(button.dataset.tabScroll) * distance, behavior: 'instant' });
    }, { signal: listeners.signal });
    dock.addEventListener('scroll', updateOverflow, { capture: true, passive: true, signal: listeners.signal });
    dock.addEventListener('dragstart', event => {
        const tab = event.target.closest?.('.terminal-dock-tab-select');
        if (!tab || !list?.contains(tab) || dock.inert || pending) {
            if (tab) event.preventDefault();
            return;
        }
        dragged = tab.closest('.terminal-dock-tab');
        dragged.classList.add('dragging');
        event.dataTransfer.effectAllowed = 'move';
        // A native drag needs a payload in Firefox. Identity is kept locally, never accepted from another document.
        event.dataTransfer.setData('text/plain', dragged.dataset.terminalId);
    }, { signal: listeners.signal });
    dock.addEventListener('dragover', event => {
        if (!dragged || !list || dock.inert || !list.contains(event.target)) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        dragX = event.clientX;
        if (scrollTimer === null) autoScroll();
        updateDrop(dragX);
    }, { signal: listeners.signal });
    dock.addEventListener('dragleave', event => {
        if (list && !list.contains(event.relatedTarget)) {
            dragX = null;
            clearDrop();
        }
    }, { signal: listeners.signal });
    dock.addEventListener('drop', event => {
        if (!dragged || !dropTarget || !list?.contains(event.target) || dock.inert) {
            clearDrag();
            return;
        }
        event.preventDefault();
        const source = dragged, target = dropTarget, after = dropAfter;
        const restoreFocus = source.contains(document.activeElement);
        clearDrag();
        void reorder(source, target, after, restoreFocus);
    }, { signal: listeners.signal });
    dock.addEventListener('dragend', clearDrag, { signal: listeners.signal });
    dock.addEventListener('keydown', event => {
        const tab = event.target.closest?.('.terminal-dock-tab-select');
        if (!tab || !list || dock.inert || !event.altKey || !event.shiftKey || event.ctrlKey || event.metaKey ||
            event.isComposing || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        event.stopPropagation();
        const tabs = groups();
        const source = tab.closest('.terminal-dock-tab');
        const forward = (event.key === 'ArrowRight') !== isRtl();
        const target = tabs[tabs.indexOf(source) + (forward ? 1 : -1)];
        if (target && !event.repeat) void reorder(source, target, forward, true);
    }, { signal: listeners.signal });
    observer.observe(dock, { childList: true, subtree: true, attributes: true, attributeFilter: ['aria-selected', 'inert'] });
    refresh();
    return () => {
        disposed = true;
        clearDrag();
        listeners.abort();
        resizeObserver.disconnect();
        observer.disconnect();
    };
}
