// Pointer and keyboard resizing for the resource list on the left of the resources layout.
//
// The width is applied to the layout element as the --resource-pane-expanded-width custom property while dragging, so
// resizing doesn't wait for a server round trip. The final width is sent to .NET, which persists it and renders the
// same value, so later renders don't reset it.
//
// Pointer capture is used so the drag survives the pointer leaving the narrow handle.

const registrations = new WeakMap();

// Priority+ overflow for the tab strip in the header. Tabs that no longer fit are marked with data-tab-overflow (hidden
// by CSS) from the right, and their ids are reported to .NET, which lists them in the "..." menu. Hiding is done in the
// DOM, rather than by rendering, so resizing never waits for a server round trip.
let tabOverflow = null;

export function registerTabOverflow(dotNetRef) {
    unregisterTabOverflow();

    const state = { dotNetRef, nav: null, resizeObserver: null, tabsObserver: null, reported: "", frame: 0 };
    const schedule = () => {
        if (!state.frame) {
            state.frame = requestAnimationFrame(() => {
                state.frame = 0;
                attach();
            });
        }
    };

    function attach() {
        // The header is rendered through a section outlet, so the strip can be created or replaced after this runs.
        const nav = document.querySelector(".resource-tabs-nav");
        if (nav !== state.nav) {
            state.resizeObserver.disconnect();
            state.tabsObserver.disconnect();
            state.nav = nav;
            state.reported = "";
            if (nav) {
                state.resizeObserver.observe(nav);
                const tablist = nav.querySelector(".resources-tab-header");
                if (tablist) {
                    // Attributes are filtered so setting data-tab-overflow below doesn't re-trigger a measurement.
                    state.tabsObserver.observe(tablist, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["hidden", "style", "class"] });
                }
            }
        }
        measure();
    }

    function measure() {
        const nav = state.nav;
        const tablist = nav?.querySelector(".resources-tab-header");
        if (!nav || !tablist) {
            return;
        }

        const tabs = Array.from(tablist.querySelectorAll("fluent-tab"));
        tabs.forEach(tab => tab.removeAttribute("data-tab-overflow"));

        const visibleTabs = tabs.filter(tab => tab.offsetWidth > 0);
        let hiddenIds = [];
        if (visibleTabs.length > 1 && getComputedStyle(tablist).display !== "none") {
            const left = visibleTabs[0].getBoundingClientRect().left;
            const widthThrough = index => visibleTabs[index].getBoundingClientRect().right - left;
            const available = nav.clientWidth;
            if (widthThrough(visibleTabs.length - 1) > available) {
                // Reserve room for the "..." button, then keep as many tabs from the left as fit.
                const budget = available - 44;
                let keep = 1;
                while (keep < visibleTabs.length && widthThrough(keep) <= budget) {
                    keep++;
                }
                visibleTabs.slice(keep).forEach(tab => tab.setAttribute("data-tab-overflow", ""));
                hiddenIds = visibleTabs.slice(keep).map(tab => tab.id);
            }
        }

        const key = hiddenIds.join(",");
        if (key !== state.reported) {
            state.reported = key;
            state.dotNetRef.invokeMethodAsync("SetOverflowTabs", hiddenIds).catch(() => { });
        }
    }

    state.resizeObserver = new ResizeObserver(schedule);
    state.tabsObserver = new MutationObserver(schedule);
    // Cheap check on any DOM change (the terminal updates constantly): only a replaced strip needs attaching again.
    state.bodyObserver = new MutationObserver(() => {
        if (document.querySelector(".resource-tabs-nav") !== state.nav) {
            schedule();
        }
    });
    state.bodyObserver.observe(document.body, { childList: true, subtree: true });
    window.addEventListener("resize", schedule);
    state.schedule = schedule;
    tabOverflow = state;
    schedule();
}

export function unregisterTabOverflow() {
    const state = tabOverflow;
    if (!state) {
        return;
    }
    tabOverflow = null;
    cancelAnimationFrame(state.frame);
    state.resizeObserver.disconnect();
    state.tabsObserver.disconnect();
    state.bodyObserver.disconnect();
    window.removeEventListener("resize", state.schedule);
}

export function registerPaneResizer(layoutElement, dotNetRef, minimumWidth, maximumWidth) {
    unregisterPaneResizer(layoutElement);

    const pane = layoutElement.querySelector(':scope > .resource-pane');
    const handle = pane?.querySelector(':scope > .resource-pane-resizer');
    if (!pane || !handle) {
        return;
    }

    let pointerId = null;
    let startX = 0;
    let startWidth = 0;
    let width = Math.round(pane.getBoundingClientRect().width);
    let disposed = false;

    // The pane never takes more than half of the window, so the content next to it stays usable.
    const bounds = () => {
        const max = Math.max(minimumWidth, Math.min(maximumWidth, Math.floor(window.innerWidth / 2)));
        return { min: minimumWidth, max };
    };

    const resizeTo = requestedWidth => {
        const { min, max } = bounds();
        const nextWidth = Math.max(min, Math.min(max, Math.round(requestedWidth)));
        if (nextWidth === width) {
            return false;
        }
        width = nextWidth;
        layoutElement.style.setProperty('--resource-pane-expanded-width', `${width}px`);
        handle.setAttribute('aria-valuenow', `${width}`);
        return true;
    };

    const commit = () => {
        if (disposed) {
            return;
        }
        dotNetRef.invokeMethodAsync('SetPaneWidthAsync', width).catch(error => {
            if (!disposed) {
                console.error('Failed to save the resource list width.', error);
            }
        });
    };

    const getActionsWidth = () => {
        const actions = pane.querySelectorAll(
            '.resource-row-action-priority-0, .resource-row-action-priority-1, ' +
            '.resource-row-action-priority-2, .resource-row-action-priority-3');
        if (actions.length === 0) {
            return width;
        }

        const { min, max } = bounds();
        let lower = min;
        let upper = max;
        let fittedWidth = max;

        // Let the pane's container queries determine the smallest width where every row action is visible.
        // Reading computed styles after each width change forces the browser to evaluate those rules.
        while (lower <= upper) {
            const candidate = Math.floor((lower + upper) / 2);
            layoutElement.style.setProperty('--resource-pane-expanded-width', `${candidate}px`);

            if ([...actions].every(action => getComputedStyle(action).display !== 'none')) {
                fittedWidth = candidate;
                upper = candidate - 1;
            }
            else {
                lower = candidate + 1;
            }
        }

        // The probing above leaves the last candidate in place. Restore the current width so a fit that changes
        // nothing doesn't leave the pane at a probed width.
        layoutElement.style.setProperty('--resource-pane-expanded-width', `${width}px`);
        return fittedWidth;
    };

    const onPointerDown = e => {
        if (e.button !== 0 || !e.isPrimary) {
            return;
        }
        pointerId = e.pointerId;
        startX = e.clientX;
        startWidth = Math.round(pane.getBoundingClientRect().width);
        width = startWidth;
        handle.setPointerCapture(e.pointerId);
        handle.classList.add('resizing');
        document.body.style.cursor = 'col-resize';
        document.body.style.userSelect = 'none';
        e.preventDefault();
    };

    const onPointerMove = e => {
        if (pointerId !== e.pointerId) {
            return;
        }
        resizeTo(startWidth + e.clientX - startX);
    };

    const onPointerEnd = e => {
        if (pointerId !== e.pointerId) {
            return;
        }
        pointerId = null;
        if (handle.hasPointerCapture(e.pointerId)) {
            handle.releasePointerCapture(e.pointerId);
        }
        handle.classList.remove('resizing');
        document.body.style.cursor = '';
        document.body.style.userSelect = '';
        if (width !== startWidth) {
            commit();
        }
    };

    // Follow the window splitter pattern: https://www.w3.org/WAI/ARIA/apg/patterns/windowsplitter/.
    // The pane is on the left of the separator, so moving the separator right makes it wider.
    const onKeyDown = e => {
        if (e.ctrlKey || e.altKey || e.metaKey || e.isComposing) {
            return;
        }
        const step = e.shiftKey ? 50 : 10;
        const { min, max } = bounds();
        let nextWidth;
        switch (e.key) {
            case 'ArrowLeft':
                nextWidth = width - step;
                break;
            case 'ArrowRight':
                nextWidth = width + step;
                break;
            case 'Home':
                nextWidth = min;
                break;
            case 'End':
                nextWidth = max;
                break;
            default:
                return;
        }
        e.preventDefault();
        e.stopPropagation();
        if (resizeTo(nextWidth)) {
            commit();
        }
    };

    // Returns false when there were no row actions to fit to yet, so the caller can try again once rows render.
    const fitToActions = (expandOnly = false) => {
        if (pane.querySelector('.resource-row-action-priority-0') === null) {
            return false;
        }

        const actionsWidth = getActionsWidth();
        if (expandOnly && actionsWidth <= width) {
            return true;
        }
        if (resizeTo(actionsWidth) || actionsWidth === width) {
            commit();
        }
        return true;
    };

    // Double-clicking fits the pane to the action disclosure rules instead of duplicating their breakpoints here.
    const onDoubleClick = e => {
        e.preventDefault();
        fitToActions();
    };

    handle.addEventListener('pointerdown', onPointerDown);
    handle.addEventListener('pointermove', onPointerMove);
    handle.addEventListener('pointerup', onPointerEnd);
    handle.addEventListener('pointercancel', onPointerEnd);
    handle.addEventListener('keydown', onKeyDown);
    handle.addEventListener('dblclick', onDoubleClick);

    registrations.set(layoutElement, {
        fitToActions,
        unregister: () => {
            disposed = true;
            handle.removeEventListener('pointerdown', onPointerDown);
            handle.removeEventListener('pointermove', onPointerMove);
            handle.removeEventListener('pointerup', onPointerEnd);
            handle.removeEventListener('pointercancel', onPointerEnd);
            handle.removeEventListener('keydown', onKeyDown);
            handle.removeEventListener('dblclick', onDoubleClick);
            if (pointerId !== null) {
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
            }
        }
    });
}

export function fitPaneToActions(layoutElement) {
    const registration = registrations.get(layoutElement);
    if (!registration) {
        return false;
    }
    return registration.fitToActions(true);
}

export function setTabListAriaLabel(tabId, label) {
    document.getElementById(tabId)?.closest("fluent-tablist")?.setAttribute("aria-label", label);
}

export function unregisterPaneResizer(layoutElement) {
    if (!layoutElement) {
        return;
    }
    const registration = registrations.get(layoutElement);
    if (registration) {
        registrations.delete(layoutElement);
        registration.unregister();
    }
}

const orderingRegistrations = new WeakMap();

export function registerResourceOrdering(layoutElement, dotNetRef) {
    unregisterResourceOrdering(layoutElement);

    let draggedItem = null;
    let pending = false;
    let disposed = false;
    const resourceSelector = '.resource-row[data-resource-name]';
    const groupSelector = '.resource-tag-group[data-resource-group-key]';
    const handleSelector = '[data-resource-drag-handle], [data-resource-group-drag-handle]';
    const isGroup = item => item?.matches(groupSelector) === true;
    const getItem = (element, group) => element?.closest?.(group ? groupSelector : resourceSelector);
    const getHandleItem = handle => getItem(handle, handle?.hasAttribute('data-resource-group-drag-handle'));
    const getKey = item => isGroup(item) ? item.dataset.resourceGroupKey : item.dataset.resourceName;
    const isEnabled = item => {
        const pane = item?.closest('.resource-pane-list');
        return (isGroup(item) ? pane?.dataset.groupReorderEnabled : pane?.dataset.reorderEnabled) === 'true';
    };
    const canMove = (source, target) => source !== target && isEnabled(source) && isEnabled(target) &&
        isGroup(source) === isGroup(target) &&
        (isGroup(source) ? source.dataset.resourceGrouping === target.dataset.resourceGrouping
            : source.dataset.resourceParent === target.dataset.resourceParent);
    const clearIndicator = () => {
        layoutElement.querySelectorAll('.resource-drop-before, .resource-drop-after').forEach(item => {
            item.classList.remove('resource-drop-before', 'resource-drop-after');
        });
    };
    const endDrag = () => {
        clearIndicator();
        draggedItem?.classList.remove('resource-dragging');
        draggedItem = null;
    };
    const commit = async (source, target, after) => {
        if (pending || !canMove(source, target)) {
            return;
        }
        const sourceKey = getKey(source);
        const group = isGroup(source);
        pending = true;
        try {
            const moved = group
                ? await dotNetRef.invokeMethodAsync('ReorderGroupAsync', source.dataset.resourceGrouping, sourceKey, getKey(target), after)
                : await dotNetRef.invokeMethodAsync('ReorderResourceAsync', sourceKey, getKey(target), after);
            if (moved && !disposed) {
                const keyAttribute = group ? 'data-resource-group-key' : 'data-resource-name';
                const handleAttribute = group ? 'data-resource-group-drag-handle' : 'data-resource-drag-handle';
                layoutElement.querySelector(`[${keyAttribute}="${CSS.escape(sourceKey)}"] [${handleAttribute}]`)?.focus();
            }
        } catch (error) {
            if (!disposed) {
                console.error('Failed to reorder resources or groups.', error);
            }
        } finally {
            pending = false;
        }
    };
    const onDragStart = event => {
        const handle = event.target.closest(handleSelector);
        const item = getHandleItem(handle);
        if (!item || !isEnabled(item) || pending) {
            return;
        }
        draggedItem = item;
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', getKey(item));
        item.classList.add('resource-dragging');
    };
    const onDragOver = event => {
        clearIndicator();
        const target = getItem(event.target, isGroup(draggedItem));
        if (!draggedItem || !target || pending || !canMove(draggedItem, target)) {
            return;
        }
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        const bounds = target.getBoundingClientRect();
        target.classList.add(event.clientY >= bounds.top + bounds.height / 2 ? 'resource-drop-after' : 'resource-drop-before');
    };
    const onDragLeave = event => {
        const target = getItem(event.target, isGroup(draggedItem));
        if (target && !target.contains(event.relatedTarget)) {
            clearIndicator();
        }
    };
    const onDrop = event => {
        const source = draggedItem;
        const target = getItem(event.target, isGroup(source));
        if (source && target && canMove(source, target)) {
            event.preventDefault();
            const bounds = target.getBoundingClientRect();
            void commit(source, target, event.clientY >= bounds.top + bounds.height / 2);
        }
        endDrag();
    };
    const onKeyDown = event => {
        if (!event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.isComposing ||
            (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) {
            return;
        }
        const item = getHandleItem(event.target.closest(handleSelector));
        if (!item || !isEnabled(item)) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        const siblings = [...item.closest('.resource-pane-list').querySelectorAll(isGroup(item) ? groupSelector : resourceSelector)]
            .filter(sibling => isGroup(item) || sibling.dataset.resourceParent === item.dataset.resourceParent);
        const after = event.key === 'ArrowDown';
        const target = siblings[siblings.indexOf(item) + (after ? 1 : -1)];
        if (target) {
            void commit(item, target, after);
        }
    };

    const handlers = { dragstart: onDragStart, dragover: onDragOver, dragleave: onDragLeave, drop: onDrop, dragend: endDrag, keydown: onKeyDown };
    for (const [name, handler] of Object.entries(handlers)) {
        layoutElement.addEventListener(name, handler);
    }
    orderingRegistrations.set(layoutElement, () => {
        disposed = true;
        endDrag();
        for (const [name, handler] of Object.entries(handlers)) {
            layoutElement.removeEventListener(name, handler);
        }
    });
}

export function unregisterResourceOrdering(layoutElement) {
    const unregister = orderingRegistrations.get(layoutElement);
    if (unregister) {
        orderingRegistrations.delete(layoutElement);
        unregister();
    }
}
