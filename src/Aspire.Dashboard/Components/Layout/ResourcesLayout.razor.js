// Pointer and keyboard resizing for the resource list on the left of the resources layout.
//
// The width is applied to the layout element as the --resource-pane-expanded-width custom property while dragging, so
// resizing doesn't wait for a server round trip. The final width is sent to .NET, which persists it and renders the
// same value, so later renders don't reset it.
//
// Pointer capture is used so the drag survives the pointer leaving the narrow handle.

const registrations = new WeakMap();
const activityRegistrations = new WeakMap();

export function usesCommandSelectionModifier() {
    // MacIntel also covers iPadOS's desktop browsing mode.
    const platform = navigator.userAgentData?.platform || navigator.platform;
    return /^(mac|iphone|ipad|ipod|ios)/i.test(platform);
}

export function registerPaneActivityVisibility(layoutElement, dotNetRef) {
    unregisterPaneActivityVisibility(layoutElement);
    let frame = 0;
    let reported;
    let forceRefresh = false;
    const report = (visible, force = false) => {
        if (visible === reported && !force) {
            return;
        }
        reported = visible;
        dotNetRef.invokeMethodAsync('SetPaneActivityVisible', visible).catch(error => {
            console.error('Unable to update resource pane activity visibility.', error);
        });
    };
    const schedule = (force = false) => {
        forceRefresh ||= force;
        if (frame) {
            return;
        }
        frame = requestAnimationFrame(() => {
            frame = 0;
            const pane = layoutElement.querySelector(':scope > .resource-pane');
            // Match the activity disclosure boundary, not the saved compact sidebar width.
            const visible = pane !== null && document.visibilityState === 'visible' &&
                layoutElement.classList.contains('pane-full-canvas') &&
                pane.getBoundingClientRect().width >= 1440;
            const force = forceRefresh;
            forceRefresh = false;
            report(visible, force);
        });
    };
    const resume = () => {
        layoutElement.toggleAttribute('data-document-hidden', document.visibilityState !== 'visible');
        if (document.visibilityState !== 'visible') {
            // Hidden tabs can suspend animation frames, so cancellation cannot wait
            // for the next measurement frame.
            cancelAnimationFrame(frame);
            frame = 0;
            forceRefresh = false;
            report(false);
        }
        else {
            schedule(true);
        }
    };
    const reconnect = event => {
        if (event.detail.state === 'hide') {
            resume();
        }
    };
    const reconnectModal = document.getElementById('components-reconnect-modal');
    const resize = new ResizeObserver(() => schedule());
    let observedPane = null;
    const observePane = () => {
        const pane = layoutElement.querySelector(':scope > .resource-pane');
        if (pane !== observedPane) {
            if (observedPane) {
                resize.unobserve(observedPane);
            }
            observedPane = pane;
            if (pane) {
                resize.observe(pane);
            }
        }
    };
    const presentation = new MutationObserver(() => {
        observePane();
        schedule();
    });
    // Grid-track animations resize the pane without resizing its workspace.
    // Collapse can remove the pane, so rebind its observation when it is remounted.
    resize.observe(layoutElement);
    observePane();
    presentation.observe(layoutElement, { attributes: true, attributeFilter: ['class'], childList: true });
    document.addEventListener('visibilitychange', resume);
    window.addEventListener('pageshow', resume);
    window.addEventListener('focus', resume);
    reconnectModal?.addEventListener('components-reconnect-state-changed', reconnect);
    activityRegistrations.set(layoutElement, () => {
        cancelAnimationFrame(frame);
        resize.disconnect();
        presentation.disconnect();
        document.removeEventListener('visibilitychange', resume);
        window.removeEventListener('pageshow', resume);
        window.removeEventListener('focus', resume);
        reconnectModal?.removeEventListener('components-reconnect-state-changed', reconnect);
    });
    resume();
}

export function unregisterPaneActivityVisibility(layoutElement) {
    activityRegistrations.get(layoutElement)?.();
    activityRegistrations.delete(layoutElement);
}

const resourcePickers = new WeakMap();

export function setResourcePickerOpen(layoutElement, dotNetRef, open) {
    const picker = layoutElement.querySelector(':scope > .resource-picker-popover');
    let registration = resourcePickers.get(layoutElement);
    if (registration?.picker !== picker) {
        unregisterResourcePicker(layoutElement);
        if (!picker) {
            return;
        }

        const onToggle = event => {
            // Only the native popover's own toggle closes the picker. Nested controls can emit bubbling toggle events.
            // https://developer.mozilla.org/en-US/docs/Web/API/HTMLElement/toggle_event
            if (event.target === picker && event.newState === "closed") {
                dotNetRef.invokeMethodAsync("OnResourcePickerClosedAsync");
            }
        };
        picker.addEventListener("toggle", onToggle);
        registration = { picker, onToggle };
        resourcePickers.set(layoutElement, registration);
    }

    if (picker.matches(":popover-open") !== open) {
        picker.togglePopover(open);
    }
}

export function unregisterResourcePicker(layoutElement) {
    const registration = resourcePickers.get(layoutElement);
    if (registration) {
        registration.picker.removeEventListener("toggle", registration.onToggle);
        resourcePickers.delete(layoutElement);
    }
}

// Priority+ overflow for the tab strip in the header. Tabs that no longer fit are marked with data-tab-overflow (hidden
// by CSS) from the right, and their ids are reported to .NET, which lists them in the "..." menu. Hiding is done in the
// DOM, rather than by rendering, so resizing never waits for a server round trip.
const tabOverflowRegistrations = new Map();

export function registerTabOverflow(registrationId, dotNetRef) {
    unregisterTabOverflow(registrationId);

    const state = { dotNetRef, nav: null, tablist: null, resizeObserver: null, tabsObserver: null, reported: "", frame: 0 };
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
            state.nav = nav;
            state.reported = "";
            if (nav) {
                state.resizeObserver.observe(nav);
            }
        }
        // Navigation replaces the keyed FluentTabs without necessarily replacing its surrounding strip.
        const tablist = nav?.querySelector(".resources-tab-header") ?? null;
        if (tablist !== state.tablist) {
            state.tabsObserver.disconnect();
            state.tablist = tablist;
            if (tablist) {
                // Attributes are filtered so setting data-tab-overflow below doesn't re-trigger a measurement.
                state.tabsObserver.observe(tablist, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["hidden", "style", "class"] });
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
        const nav = document.querySelector(".resource-tabs-nav");
        if (nav !== state.nav || (nav?.querySelector(".resources-tab-header") ?? null) !== state.tablist) {
            schedule();
        }
    });
    state.bodyObserver.observe(document.body, { childList: true, subtree: true });
    window.addEventListener("resize", schedule);
    state.schedule = schedule;
    // Recording switches can mount the new layout before the old layout finishes disposing.
    // Use a per-layout ID: Blazor cannot resolve an ElementReference after its DOM element is removed.
    tabOverflowRegistrations.set(registrationId, state);
    schedule();
}

export function unregisterTabOverflow(registrationId) {
    const state = tabOverflowRegistrations.get(registrationId);
    if (!state) {
        return;
    }
    tabOverflowRegistrations.delete(registrationId);
    cancelAnimationFrame(state.frame);
    state.resizeObserver.disconnect();
    state.tabsObserver.disconnect();
    state.bodyObserver.disconnect();
    window.removeEventListener("resize", state.schedule);
}

export function registerPaneResizer(layoutElement, dotNetRef, minimumWidth, maximumWidth, defaultWidth) {
    unregisterPaneResizer(layoutElement);

    const pane = layoutElement.querySelector(':scope > .resource-pane');
    const handle = pane?.querySelector(':scope > .resource-pane-resizer');
    if (!pane || !handle) {
        return;
    }

    let pointerId = null;
    let startX = 0;
    let startWidth = 0;
    // Use the saved logical width while a full-canvas return is still animating.
    let width = Number(handle.getAttribute('aria-valuenow'));
    let pendingCommits = 0;
    let disposed = false;

    const applyWidth = () => {
        if (layoutElement.style.getPropertyValue('--resource-pane-expanded-width') !== `${width}px`) {
            layoutElement.style.setProperty('--resource-pane-expanded-width', `${width}px`);
        }
        if (handle.getAttribute('aria-valuenow') !== `${width}`) {
            handle.setAttribute('aria-valuenow', `${width}`);
        }
    };

    // A render acknowledging 430px can arrive after the next key already requested
    // 440px. Keep that in-flight client width visible without issuing another save.
    const widthObserver = new MutationObserver(() => {
        if (pendingCommits > 0) {
            applyWidth();
        }
    });
    widthObserver.observe(layoutElement, { attributes: true, attributeFilter: ['style'] });
    widthObserver.observe(handle, { attributes: true, attributeFilter: ['aria-valuenow'] });

    // The pane never takes more than half of the window, so the content next to it stays usable.
    const bounds = () => {
        const max = Math.max(minimumWidth, Math.min(maximumWidth, Math.floor(window.innerWidth / 2)));
        return { min: minimumWidth, max };
    };

    const resizeTo = requestedWidth => {
        const { min, max } = bounds();
        const nextWidth = Math.max(min, Math.min(max, Math.round(requestedWidth)));
        if (nextWidth === width &&
            handle.getAttribute('aria-valuenow') === `${nextWidth}` &&
            layoutElement.style.getPropertyValue('--resource-pane-expanded-width') === `${nextWidth}px`) {
            return false;
        }
        width = nextWidth;
        applyWidth();
        return true;
    };

    const commit = () => {
        if (disposed) {
            return;
        }
        pendingCommits++;
        dotNetRef.invokeMethodAsync('SetPaneWidthAsync', width).catch(error => {
            if (!disposed) {
                console.error('Failed to save the resource list width.', error);
            }
        }).finally(() => {
            pendingCommits--;
            if (!disposed) {
                applyWidth();
            }
        });
    };

    const onPointerDown = e => {
        if (e.button !== 0 || !e.isPrimary) {
            return;
        }
        pointerId = e.pointerId;
        handle.focus({ preventScroll: true });
        startX = e.clientX;
        startWidth = pendingCommits > 0 ? width : Number(handle.getAttribute('aria-valuenow'));
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
        // With no outstanding saves, a changed DOM value is an intentional restored
        // preference. While saving, it can instead be an older render acknowledgement.
        if (pendingCommits === 0) {
            width = Number(handle.getAttribute('aria-valuenow'));
        }
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

    const onDoubleClick = e => {
        e.preventDefault();
        handle.focus({ preventScroll: true });
        // Double-click restores the default width, which is wider than the minimum the pane can be dragged to.
        resizeTo(defaultWidth);
        commit();
    };

    handle.addEventListener('pointerdown', onPointerDown);
    handle.addEventListener('pointermove', onPointerMove);
    handle.addEventListener('pointerup', onPointerEnd);
    handle.addEventListener('pointercancel', onPointerEnd);
    handle.addEventListener('keydown', onKeyDown);
    handle.addEventListener('dblclick', onDoubleClick);
    // The separator can render before its module has loaded, or be recreated on
    // full-canvas return. Expose keyboard interaction only after handlers are ready.
    handle.tabIndex = 0;
    handle.setAttribute('aria-disabled', 'false');

    registrations.set(layoutElement, {
        unregister: () => {
            disposed = true;
            widthObserver.disconnect();
            handle.tabIndex = -1;
            handle.setAttribute('aria-disabled', 'true');
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

// Older live circuits can load this source asset before the dashboard assembly is
// rebuilt. Keep their interop call compatible without automatically changing width.
export function fitPaneToActions(_layoutElement) {
    return true;
}

export function setTabListAriaLabel(tabId, label) {
    document.getElementById(tabId)?.closest("fluent-tablist")?.setAttribute("aria-label", label);
}

export function isResourceTabFocused(tabId) {
    return document.activeElement?.closest("fluent-tab")?.id === tabId;
}

// The last tab the user pressed, clicked or chose with Enter or Space (see registerPaneInteractions). The tab strip is
// re-created whenever the location changes, so a click that lands just as a navigation finishes reaches the layout
// from the outgoing strip, along with the change events that strip raises as it's torn down. Only a real activation
// tells the click apart from that noise.
let recentTabActivation = null;

// Returns whether the user activated the tab in the last second, and forgets the activation so it's used once.
export function consumeRecentTabActivation(tabId) {
    const activation = recentTabActivation;
    if (activation && activation.id === tabId && performance.now() - activation.time < 1000) {
        recentTabActivation = null;
        return true;
    }
    return false;
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

const contextMenuRegistrations = new WeakMap();

export function registerResourceContextMenu(layoutElement, dotNetRef) {
    unregisterResourceContextMenu(layoutElement);

    const state = { trigger: null, selectionTrigger: null, disposed: false };
    // A popover opened from a row, such as the URLs column's list of URLs, renders inside the row. Clicking its
    // text mustn't select the row either. FluentPopover's host element (fluent-popover-b) is the ancestor a click in
    // its content finds: the element with the popover attribute is in its shadow root.
    const interactiveSelector = 'a, button, fluent-button, fluent-menu-item, input, select, textarea, .resource-relationship-summary, [role="button"], [role="checkbox"], [contenteditable], [popover], fluent-popover-b';
    const open = (event, keyboard) => {
        const row = event.target.closest('.resource-pane-list .resource-row[data-resource-name]');
        const link = row?.querySelector('.resource-row-link');
        const control = event.target.closest(interactiveSelector);
        // Nested links and controls retain their native menus and keyboard behavior. Telemetry-only
        // rows have no app-model actions, so keep their native context menu.
        if (!link || (keyboard && event.target !== link) || (control && control !== link)) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        state.trigger = link;
        state.restoreAfterEscape = false;
        const bounds = link.getBoundingClientRect();
        const x = keyboard ? bounds.left : event.clientX;
        const y = keyboard ? bounds.bottom : event.clientY;
        dotNetRef.invokeMethodAsync('OpenResourceContextMenuAsync', row.dataset.resourceName, Math.round(x), Math.round(y))
            .catch(error => {
                if (!state.disposed) {
                    console.error('Failed to open the resource context menu.', error);
                }
            });
    };
    const onContextMenu = event => open(event, event.button === 0);
    const onContextMenuDismiss = event => {
        if (event.key === 'Escape' &&
            document.querySelector('fluent-menu[aria-labelledby="resource-pane-context-menu-header"] fluent-menu-list:popover-open')) {
            state.restoreAfterEscape = true;
        }
    };
    const onIdentityInteraction = event => {
        const link = event.target.closest('.resource-row-link');
        const name = link?.querySelector('.resource-row-name');
        // A compact sidebar row's type and state columns give way to its actions while the row is
        // hovered or focused, where rows can be hovered (see ResourcesLayout.razor.css). The row's
        // tooltip names them instead, from the full text the hidden columns keep in data-tooltip:
        //   <span class="resource-row-meta">
        //     <span class="resource-row-type" data-tooltip="Container (persistent)">…</span>
        //     <span class="resource-row-meta-state" data-tooltip="Running (Unhealthy)">…</span>
        //   </span>
        // The columns may not be hidden yet when the event arrives, so this decides from the layout
        // (compact, sidebar, a hovering pointer) rather than from their visibility.
        const meta = link?.querySelector('.resource-row-meta');
        const metaText = meta && !link.closest('.pane-full-canvas') && window.matchMedia('(hover: hover)').matches
            ? [...meta.querySelectorAll('[data-tooltip]')].map(el => el.getAttribute('data-tooltip')).filter(Boolean).join(' · ')
            : null;
        // Keep availability explanations even when the name fits. Clipped names also
        // need their full identity; other available rows remain quiet.
        const tooltip = [
            name && name.scrollWidth > name.clientWidth ? link.getAttribute('aria-label') : null,
            metaText,
            link?.getAttribute('data-resource-unavailable-reason')
        ].filter(Boolean).join('\n');
        if (tooltip) {
            link.setAttribute('data-tooltip', tooltip);
            link.setAttribute('data-tooltip-positioning', 'above');
        }
        else if (link) {
            link.removeAttribute('data-tooltip');
            link.removeAttribute('data-tooltip-positioning');
        }
    };
    const onShiftPointerDown = event => {
        const link = event.target.closest('.resource-row-link');
        if (event.button !== 0 || !event.shiftKey || !link) {
            return;
        }
        // A pre-existing selection in details can otherwise extend across the entire
        // document, even though row text itself has user-select:none. Do not cancel
        // the subsequent click: Blazor still owns resource range selection.
        event.preventDefault();
        window.getSelection()?.removeAllRanges();
        link.focus({ preventScroll: true });
    };
    const onClick = event => {
        const selectedLink = event.target.closest('.resource-row-link');
        if (event.button === 0 && event.shiftKey && event.detail > 0 && selectedLink) {
            window.getSelection()?.removeAllRanges();
            queueMicrotask(() => window.getSelection()?.removeAllRanges());
        }
        if (layoutElement.classList.contains('pane-full-canvas') && event.button === 0 && selectedLink) {
            // Capture the initiating control before asynchronous navigation. By the time
            // the reveal starts, the user may already have focused another control.
            state.selectionTrigger = selectedLink;
        }
        if (!layoutElement.classList.contains('pane-full-canvas') || event.button !== 0 || event.detail > 1 || event.defaultPrevented ||
            event.target.closest(interactiveSelector)) {
            return;
        }
        const row = event.target.closest('.resource-pane-list .resource-row[data-resource-name], .resource-pane-list .resource-row-kind-telemetry');
        const link = row?.querySelector('.resource-row-link');
        const selection = window.getSelection();
        if (!link || (selection && !selection.isCollapsed && row.contains(selection.anchorNode))) {
            return;
        }
        // Keep one native link as the keyboard/accessible target, and route the rest
        // of its row through the same selection handler, including Ctrl/Shift ranges.
        event.preventDefault();
        link.focus({ preventScroll: true });
        link.dispatchEvent(new MouseEvent('click', {
            bubbles: true, cancelable: true, view: window,
            ctrlKey: event.ctrlKey, metaKey: event.metaKey, shiftKey: event.shiftKey, altKey: event.altKey
        }));
    };
    const onKeyDown = event => {
        if (!event.isComposing && !event.ctrlKey && !event.altKey && !event.metaKey &&
            (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10'))) {
            open(event, true);
        }
        else if (event.target.matches('.resource-row[data-resource-tree-row] > .resource-row-link') &&
            !event.isComposing && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey &&
            (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
            const row = event.target.closest('.resource-row');
            const toggle = row.querySelector('.resource-row-toggle[aria-expanded]');
            const expanded = toggle?.getAttribute('aria-expanded') === 'true';
            if (toggle && ((event.key === 'ArrowLeft' && expanded) || (event.key === 'ArrowRight' && !expanded))) {
                event.preventDefault();
                toggle.click();
            }
            else {
                const target = event.key === 'ArrowLeft'
                    ? [...layoutElement.querySelectorAll('.resource-row[data-resource-name]')]
                        .find(candidate => candidate.dataset.resourceName === row.dataset.resourceParent)?.querySelector('.resource-row-link')
                    : expanded ? row.nextElementSibling?.querySelector('.resource-row-link') : null;
                if (target) {
                    event.preventDefault();
                    target.focus();
                }
            }
        }
        else if (layoutElement.classList.contains('pane-full-canvas') &&
            event.target.matches('.resource-row-link') && event.key === ' ' &&
            !event.isComposing && !event.repeat && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
            event.preventDefault();
            event.target.click();
        }
    };
    // Escape closes the resource content, as the header's close button does, when nothing else uses the key. Menus,
    // popovers and dialogs close themselves with it, and text fields and the terminal keep it. Some of them close in
    // their own keydown handler, so whether the key is free is decided in the capture phase, before any of them runs,
    // and the button is clicked in the bubble phase, only if no handler took the key (preventDefault). The capture
    // listener is on the window, which comes before the document: the tooltip provider removes a shown tooltip in its
    // own capture listener on the document.
    let escapeIsFree = false;
    const escapeOwnerSelector = 'input, textarea, select, [contenteditable=""], [contenteditable="true"], .xterm, ' +
        '[popover], fluent-popover-b, fluent-menu, fluent-dialog, dialog, [role="dialog"], [role="menu"], [role="listbox"], [role="combobox"], [role="textbox"]';
    const hasOpenOverlay = () => {
        // A control whose popup is open says so (aria-haspopup with aria-expanded="true"), whatever the popup is built
        // with: the filter button's popover isn't a native popover, and focus stays on the button after clicking it.
        // A tree toggle has aria-expanded but no popup, so it doesn't count.
        if (document.querySelector('[aria-haspopup]:not([aria-haspopup="false"])[aria-expanded="true"]')) {
            return true;
        }
        // A shown tooltip is dismissed by Escape (see app-tooltips.js), and the same key mustn't also close the content.
        // https://www.w3.org/WAI/WCAG22/Understanding/content-on-hover-or-focus.html
        if (document.querySelector('aspire-tooltip-provider fluent-tooltip:not([aria-hidden="true"])')) {
            return true;
        }
        // A FluentPopover's dialog is in its shadow root, out of reach of the query below, and the button opening it
        // doesn't always say so, like the URLs column's +N button. Its host says whether it's open:
        //   <fluent-popover-b class="content-list-popover url-overflow-popover" anchor-id="…-more" opened="true">
        if (document.querySelector('fluent-popover-b[opened="true"]')) {
            return true;
        }
        try {
            // Hidden tooltips can stay in the popover layer, so they're left to the check above.
            return [...document.querySelectorAll(':popover-open, dialog[open]')]
                .some(element => element.localName !== 'fluent-tooltip' && !element.closest('aspire-tooltip-provider'));
        }
        catch {
            // Browsers without the popover API have no open popovers to protect.
            return false;
        }
    };
    const onEscapeCapture = event => {
        escapeIsFree = event.key === 'Escape' && !event.isComposing && !event.repeat &&
            !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey &&
            !event.composedPath().some(target => target instanceof Element && target.matches(escapeOwnerSelector)) &&
            !hasOpenOverlay();
    };
    const onEscape = event => {
        const isFree = escapeIsFree;
        escapeIsFree = false;
        // Fluent's tab list cancels the keys it receives, Escape included, without doing anything with Escape. So a
        // focused tab, for example the one just clicked to open the content, doesn't keep Escape from closing it.
        const isFromTabList = event.composedPath().some(target => target instanceof Element && target.matches('[role="tablist"]'));
        if (!isFree || (event.defaultPrevented && !isFromTabList)) {
            return;
        }
        // The header renders the button only while there's content to close (see DetailPageCloseButton).
        const close = layoutElement.querySelector('.resource-header-controls .detail-page-close');
        if (close && close.getClientRects().length > 0 && !close.closest('[inert]')) {
            event.preventDefault();
            close.click();
        }
    };

    // While the resource list fills the page, no tab is shown selected, and choosing the current tab opens its content.
    // The tab list can't report that choice (see OpenTabFromFullCanvasAsync), so a click, Enter or Space on a tab is
    // passed on. The tabs are in the page header, outside the layout, so this listens on the document.
    const onTabActivate = event => {
        if (!layoutElement.classList.contains('pane-full-canvas') ||
            (event.type === 'keydown' && (event.isComposing || (event.key !== 'Enter' && event.key !== ' ')))) {
            return;
        }
        const tab = event.target instanceof Element ? event.target.closest('.resources-tab-header fluent-tab[id^="tab-"]') : null;
        if (tab) {
            dotNetRef.invokeMethodAsync('OpenTabFromFullCanvasAsync', tab.id).catch(() => { });
        }
    };

    const onTabUserActivation = event => {
        if (event.type === 'keydown' && (event.isComposing || (event.key !== 'Enter' && event.key !== ' '))) {
            return;
        }
        const tab = event.target instanceof Element ? event.target.closest('.resources-tab-header fluent-tab[id^="tab-"]') : null;
        if (tab) {
            recentTabActivation = { id: tab.id, time: performance.now() };
        }
    };

    layoutElement.addEventListener('contextmenu', onContextMenu);
    layoutElement.addEventListener('pointerdown', onShiftPointerDown, true);
    document.addEventListener('pointerdown', onTabUserActivation, true);
    document.addEventListener('keydown', onTabUserActivation, true);
    layoutElement.addEventListener('mousedown', onShiftPointerDown, true);
    document.addEventListener('keydown', onContextMenuDismiss, true);
    window.addEventListener('keydown', onEscapeCapture, true);
    document.addEventListener('keydown', onEscape);
    document.addEventListener('click', onTabActivate);
    document.addEventListener('keydown', onTabActivate);
    layoutElement.addEventListener('click', onClick);
    layoutElement.addEventListener('keydown', onKeyDown);
    layoutElement.addEventListener('mouseover', onIdentityInteraction);
    layoutElement.addEventListener('focusin', onIdentityInteraction);
    state.unregister = () => {
        state.disposed = true;
        layoutElement.removeEventListener('contextmenu', onContextMenu);
        layoutElement.removeEventListener('pointerdown', onShiftPointerDown, true);
        document.removeEventListener('pointerdown', onTabUserActivation, true);
        document.removeEventListener('keydown', onTabUserActivation, true);
        layoutElement.removeEventListener('mousedown', onShiftPointerDown, true);
        document.removeEventListener('keydown', onContextMenuDismiss, true);
        window.removeEventListener('keydown', onEscapeCapture, true);
        document.removeEventListener('keydown', onEscape);
        document.removeEventListener('click', onTabActivate);
        document.removeEventListener('keydown', onTabActivate);
        layoutElement.removeEventListener('click', onClick);
        layoutElement.removeEventListener('keydown', onKeyDown);
        layoutElement.removeEventListener('mouseover', onIdentityInteraction);
        layoutElement.removeEventListener('focusin', onIdentityInteraction);
    };
    contextMenuRegistrations.set(layoutElement, state);
}

export function restoreResourceContextMenuFocus(layoutElement) {
    const state = contextMenuRegistrations.get(layoutElement);
    const trigger = state?.trigger;
    const restoreAfterEscape = state?.restoreAfterEscape;
    if (state) {
        state.trigger = null;
        state.restoreAfterEscape = false;
    }
    const active = document.activeElement;
    // Light dismissal can focus another control. Only restore focus if it was left in
    // the dismissed menu (or dropped on the body), not after an intentional focus move.
    if (trigger?.isConnected && (restoreAfterEscape || active === document.body ||
        active?.closest('fluent-menu')?.getAttribute('aria-labelledby') === 'resource-pane-context-menu-header')) {
        trigger.focus({ preventScroll: true });
    }
}

export function unregisterResourceContextMenu(layoutElement) {
    const state = contextMenuRegistrations.get(layoutElement);
    if (state) {
        contextMenuRegistrations.delete(layoutElement);
        state.unregister();
    }
}

const selectionReveals = new WeakMap();

export function cancelPaneSelectionReveal(layoutElement) {
    const state = selectionReveals.get(layoutElement);
    if (state) {
        selectionReveals.delete(layoutElement);
        cancelAnimationFrame(state.frame);
        state.observer.disconnect();
    }
}

export function startPaneSelectionReveal(layoutElement, dotNetRef, location) {
    cancelPaneSelectionReveal(layoutElement);
    const interaction = contextMenuRegistrations.get(layoutElement);
    const state = { location, trigger: interaction?.selectionTrigger, frame: 0, observer: null };
    if (interaction) {
        interaction.selectionTrigger = null;
    }
    const current = () => selectionReveals.get(layoutElement) === state;
    const ownsLocation = () => layoutElement.isConnected && layoutElement.dataset.paneLocation === location &&
        !layoutElement.classList.contains('pane-full-canvas');
    const complete = () => {
        if (!current() || !ownsLocation()) {
            return;
        }
        state.observer.disconnect();
        dotNetRef.invokeMethodAsync('CompletePaneSelectionReveal', location).catch(error => {
            if (current()) {
                cancelPaneSelectionReveal(layoutElement);
                console.error('Failed to finish the selected resource reveal.', error);
            }
        });
    };
    const settle = () => {
        state.frame = requestAnimationFrame(() => {
            if (!current() || !ownsLocation()) {
                return;
            }
            // Wait for the actual grid transition, not a timer that can outlive a reversal,
            // viewport change, or reduced-motion switch. Mobile has no moving grid boundary.
            const animations = layoutElement.getAnimations().filter(animation =>
                animation.effect?.target === layoutElement && animation.transitionProperty === 'grid-template-columns');
            Promise.all(animations.map(animation => animation.finished)).then(complete, error => {
                if (!current()) {
                    return;
                }
                if (error.name === 'AbortError') {
                    settle();
                }
                else {
                    console.error('Failed to observe the selected resource reveal.', error);
                    complete();
                }
            });
        });
    };
    state.observer = new MutationObserver(() => {
        if (!ownsLocation() || !layoutElement.classList.contains('pane-selection-reveal')) {
            cancelPaneSelectionReveal(layoutElement);
        }
    });
    state.observer.observe(layoutElement, { attributes: true, attributeFilter: ['class', 'data-pane-location'] });
    selectionReveals.set(layoutElement, state);
    settle();
}

export function focusPaneSelectionReveal(layoutElement, location) {
    const state = selectionReveals.get(layoutElement);
    if (!state || state.location !== location) {
        return;
    }
    cancelPaneSelectionReveal(layoutElement);
    const content = layoutElement.querySelector('.resource-content');
    const active = document.activeElement;
    // Do not steal focus from a control the user chose during the transition.
    if (layoutElement.dataset.paneLocation !== location || layoutElement.classList.contains('pane-full-canvas') ||
        content.inert || (active !== state.trigger && active !== document.body)) {
        return;
    }
    const target = layoutElement.querySelector('.resource-title') ?? content;
    target.tabIndex = -1;
    target.focus({ preventScroll: true });
}
