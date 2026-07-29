const registrations = new WeakMap();

export function initializeSplitView(container, dotNetReference) {
    const bar = container.querySelector(":scope > .split-view-bar");
    let activePointerId = null;

    const updateSizes = (clientX, clientY) => {
        const rect = container.getBoundingClientRect();
        const horizontal = container.dataset.orientation === "horizontal";
        const barSize = horizontal ? bar.offsetWidth : bar.offsetHeight;
        const totalSize = (horizontal ? rect.width : rect.height) - barSize;
        const pointerPosition = (horizontal ? clientX - rect.left : clientY - rect.top) - (barSize / 2);
        const style = getComputedStyle(container);
        const panel1Minimum = Number.parseFloat(style.getPropertyValue("--panel-1-min-size")) || 0;
        const panel2Minimum = Number.parseFloat(style.getPropertyValue("--panel-2-min-size")) || 0;
        let panel1Size;
        if (panel1Minimum + panel2Minimum > totalSize) {
            const minimumTotal = panel1Minimum + panel2Minimum;
            panel1Size = minimumTotal === 0 ? totalSize / 2 : totalSize * (panel1Minimum / minimumTotal);
        } else {
            panel1Size = Math.max(panel1Minimum, Math.min(totalSize - panel2Minimum, pointerPosition));
        }
        const panel2Size = totalSize - panel1Size;

        container.style.setProperty("--panel-1-size", `${panel1Size}px`);
        container.style.setProperty("--panel-2-size", `${panel2Size}px`);
        return { panel1Size, panel2Size };
    };

    const onPointerDown = event => {
        if (container.classList.contains("split-view--collapsed")) {
            return;
        }

        activePointerId = event.pointerId;
        bar.setPointerCapture(event.pointerId);
        event.preventDefault();
    };

    const onPointerMove = event => {
        if (event.pointerId === activePointerId) {
            updateSizes(event.clientX, event.clientY);
        }
    };

    const onPointerUp = event => {
        if (event.pointerId !== activePointerId) {
            return;
        }

        const sizes = updateSizes(event.clientX, event.clientY);
        activePointerId = null;
        if (bar.hasPointerCapture(event.pointerId)) {
            bar.releasePointerCapture(event.pointerId);
        }
        dotNetReference.invokeMethodAsync("HandleResizeAsync", sizes.panel1Size, sizes.panel2Size);
    };

    const onKeyDown = event => {
        if (container.classList.contains("split-view--collapsed") ||
            !["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) {
            return;
        }

        const rect = container.getBoundingClientRect();
        const horizontal = container.dataset.orientation === "horizontal";
        const barRect = bar.getBoundingClientRect();
        const currentPosition = horizontal
            ? barRect.left + (barRect.width / 2)
            : barRect.top + (barRect.height / 2);
        const decrease = event.key === "ArrowLeft" || event.key === "ArrowUp";
        const nextPosition = currentPosition + (decrease ? -10 : 10);
        const sizes = updateSizes(
            horizontal ? nextPosition : rect.left,
            horizontal ? rect.top : nextPosition);

        event.preventDefault();
        dotNetReference.invokeMethodAsync("HandleResizeAsync", sizes.panel1Size, sizes.panel2Size);
    };

    bar.addEventListener("pointerdown", onPointerDown);
    bar.addEventListener("pointermove", onPointerMove);
    bar.addEventListener("pointerup", onPointerUp);
    bar.addEventListener("pointercancel", onPointerUp);
    bar.addEventListener("keydown", onKeyDown);
    registrations.set(container, { bar, onPointerDown, onPointerMove, onPointerUp, onKeyDown });
}

export function disposeSplitView(container) {
    const registration = registrations.get(container);
    if (!registration) {
        return;
    }

    const { bar, onPointerDown, onPointerMove, onPointerUp, onKeyDown } = registration;
    bar.removeEventListener("pointerdown", onPointerDown);
    bar.removeEventListener("pointermove", onPointerMove);
    bar.removeEventListener("pointerup", onPointerUp);
    bar.removeEventListener("pointercancel", onPointerUp);
    bar.removeEventListener("keydown", onKeyDown);
    registrations.delete(container);
}
