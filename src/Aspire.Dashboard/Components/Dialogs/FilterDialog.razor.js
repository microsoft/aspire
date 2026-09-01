export function showPicker(element) {
    if (!element) {
        return;
    }

    try {
        element.showPicker();
    } catch {
        // showPicker() is not supported in all browsers/element states.
        // Fall back to focusing the element so the user can interact with it directly.
        element.focus();
    }
}
