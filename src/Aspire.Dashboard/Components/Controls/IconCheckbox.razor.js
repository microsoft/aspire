export function initializeIconCheckboxKeyboard(element) {
    if (!element) {
        return;
    }

    disposeIconCheckboxKeyboard(element);

    const onKeyDown = event => {
        if (event.repeat || !isSpaceKey(event)) {
            return;
        }

        if (element.getAttribute("aria-disabled") === "true") {
            return;
        }

        // Handle Space here so Tab/Shift+Tab keep their native focus behavior while Space
        // cannot scroll the page or bubble to an enclosing grid's row activation.
        event.preventDefault();
        event.stopPropagation();
        element.click();
    };

    element.addEventListener("keydown", onKeyDown);
    element.iconCheckboxKeyDown = onKeyDown;
}

export function disposeIconCheckboxKeyboard(element) {
    const onKeyDown = element?.iconCheckboxKeyDown;
    if (!onKeyDown) {
        return;
    }

    element.removeEventListener("keydown", onKeyDown);
    delete element.iconCheckboxKeyDown;
}

function isSpaceKey(event) {
    return event.key === " " ||
        event.key === "Spacebar" ||
        event.code === "Space";
}
