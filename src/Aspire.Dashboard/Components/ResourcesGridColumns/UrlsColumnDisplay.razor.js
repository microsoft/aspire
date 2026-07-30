// Adaptive overflow measurement for the resource URLs column. Renders inline URL items and, using a
// ResizeObserver, decides how many fit as the column width changes; the first item is always kept.
// Overflowed items are hidden inline (they remain in the popover the component renders) and the
// component is told the visible count so it can update the "+N" button and popover contents.
// Colocated module: no inline script, so it's CSP-safe. The observer is disconnected in dispose().

const registrations = new WeakMap();

// Space (px) reserved for the trailing "+N" button when there is overflow. A little larger than the
// button so a partially-fitting item doesn't sit flush against it.
const MORE_BUTTON_RESERVE = 52;

export function initialize(container, dotNetReference) {
    const overflow = container.querySelector("[data-url-overflow]");
    if (!overflow) {
        return;
    }

    let lastVisible = -1;

    const measure = () => {
        const items = Array.from(overflow.querySelectorAll("[data-url-item]"));
        if (items.length === 0) {
            return;
        }

        // Reveal every item first so we can read its intrinsic width, including items hidden on the
        // previous pass. This runs inside the ResizeObserver/interop callback (before paint), so the
        // reveal-measure-hide cycle below doesn't flash on screen.
        for (const item of items) {
            item.style.display = "";
        }

        const available = overflow.clientWidth;

        let total = 0;
        for (const item of items) {
            total += item.getBoundingClientRect().width;
        }

        let visible;
        if (total <= available) {
            // Everything fits: no overflow, so no button space needs reserving.
            visible = items.length;
        } else {
            // Fit as many as possible while leaving room for the "+N" button. The first item is
            // always visible (it truncates with an ellipsis if it's wider than the column).
            const budget = available - MORE_BUTTON_RESERVE;
            let used = 0;
            visible = 0;
            for (let i = 0; i < items.length; i++) {
                const width = items[i].getBoundingClientRect().width;
                if (i === 0) {
                    used += width;
                    visible = 1;
                    continue;
                }
                if (used + width <= budget) {
                    used += width;
                    visible++;
                } else {
                    break;
                }
            }
        }

        // Apply the computed visibility synchronously so there's no flash of overflowing items.
        for (let i = 0; i < items.length; i++) {
            items[i].style.display = i < visible ? "" : "none";
        }

        if (visible !== lastVisible) {
            lastVisible = visible;
            dotNetReference.invokeMethodAsync("SetVisibleCountAsync", visible);
        }
    };

    const observer = new ResizeObserver(() => measure());
    observer.observe(overflow);

    registrations.set(container, { observer, measure });

    // Initial measure so the layout is correct before the first resize event.
    measure();
}

export function measure(container) {
    const registration = registrations.get(container);
    if (registration) {
        registration.measure();
    }
}

export function dispose(container) {
    const registration = registrations.get(container);
    if (registration) {
        registration.observer.disconnect();
        registrations.delete(container);
    }
}
