const headers = new WeakMap();

export function observeHeader(header, dotNetRef) {
    disconnectHeader(header);
    const state = { frame: 0, disposed: false, reported: "" };
    const schedule = () => {
        if (!state.disposed && !state.frame) {
            state.frame = requestAnimationFrame(() => {
                state.frame = 0;
                measure();
            });
        }
    };
    function measure() {
        const actions = [...header.querySelectorAll("[data-header-action]")];
        actions.forEach(action => action.removeAttribute("data-header-overflow"));
        header.removeAttribute("data-has-header-overflow");
        const hidden = [];
        // Let flex layout shrink the app name and page navigation first. Only move actions when the remaining
        // controls genuinely exceed the header, reserving the overflow button before choosing what still fits.
        if (header.scrollWidth > header.clientWidth) {
            header.setAttribute("data-has-header-overflow", "");
            for (const action of actions.toReversed()) {
                if (header.scrollWidth <= header.clientWidth) {
                    break;
                }
                action.setAttribute("data-header-overflow", "");
                hidden.unshift(action.getAttribute("data-header-action"));
            }
        }
        const key = hidden.join(",");
        if (key !== state.reported) {
            state.reported = key;
            dotNetRef.invokeMethodAsync("SetHeaderOverflowActions", hidden).catch(error => {
                if (!state.disposed) {
                    console.error("Failed to update dashboard header actions.", error);
                }
            });
        }
    }
    state.resize = new ResizeObserver(schedule);
    state.mutations = new MutationObserver(schedule);
    state.resize.observe(header);
    state.mutations.observe(header, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["data-header-action", "data-has-secondary-actions"] });
    document.fonts?.addEventListener("loadingdone", schedule);
    document.fonts?.ready.then(schedule);
    state.schedule = schedule;
    headers.set(header, state);
    schedule();
}

export function disconnectHeader(header) {
    const state = headers.get(header);
    if (state) {
        state.disposed = true;
        cancelAnimationFrame(state.frame);
        state.resize.disconnect();
        state.mutations.disconnect();
        document.fonts?.removeEventListener("loadingdone", state.schedule);
        headers.delete(header);
    }
}
