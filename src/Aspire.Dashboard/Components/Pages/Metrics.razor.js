const registrations = new WeakMap();

export function registerMetricsSplitter(element, dotNetRef) {
    unregisterMetricsSplitter(element);

    const splitter = element.querySelector(':scope > .fluent-multi-splitter');
    const bar = splitter?.querySelector(':scope > .fluent-multi-splitter-bar');
    const groupsPane = splitter?.querySelector(':scope > .fluent-multi-splitter-pane:has(> .metric-groups)');
    if (!splitter || !bar || !groupsPane) {
        return false;
    }

    let disposed = false;

    const fitGroupsPane = event => {
        event.preventDefault();

        const currentWidth = groupsPane.getBoundingClientRect().width;
        const hiddenLabelWidth = [...groupsPane.querySelectorAll('.metric-group-name')]
            .reduce((maximum, label) => Math.max(maximum, label.scrollWidth - label.clientWidth), 0);
        const splitterWidth = splitter.getBoundingClientRect().width;
        if (splitterWidth <= 0) {
            return;
        }

        const targetWidth = Math.min(currentWidth + hiddenLabelWidth, splitterWidth / 2);
        const targetPercent = targetWidth / splitterWidth * 100;
        dotNetRef.invokeMethodAsync('SetMetricGroupsPaneSizeAsync', targetPercent).catch(error => {
            if (!disposed) {
                console.error('Failed to fit the metrics instrument selector.', error);
            }
        });
    };

    const onDoubleClick = event => {
        const barBounds = bar.getBoundingClientRect();
        // Fluent's draggable bar is only a few pixels wide and can be covered at either edge by a pane.
        // Handle the whole visible gutter from the stable wrapper so a normal double-click does not have
        // to land on one particular internal shadow/overlay element.
        const hitPadding = 6;
        if (event.clientX < barBounds.left - hitPadding || event.clientX > barBounds.right + hitPadding) {
            return;
        }

        fitGroupsPane(event);
    };

    element.addEventListener('dblclick', onDoubleClick, true);

    registrations.set(element, () => {
        disposed = true;
        element.removeEventListener('dblclick', onDoubleClick, true);
    });

    return true;
}

export function unregisterMetricsSplitter(element) {
    registrations.get(element)?.();
    registrations.delete(element);
}
