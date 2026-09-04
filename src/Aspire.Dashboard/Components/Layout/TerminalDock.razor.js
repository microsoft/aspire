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
