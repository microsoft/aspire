export function initializeAspireTooltip(anchorId) {
    disposeAspireTooltip(anchorId);

    const anchor = document.getElementById(anchorId);
    if (!anchor) {
        return false;
    }

    // Focus and hover are tracked independently because a keyboard user can tab to focus
    // an element and then move the mouse over/off of it (or vice versa: hover, then tab
    // away). The tooltip should only hide once BOTH signals are gone, not whichever one
    // changes last - otherwise e.g. focus -> mouseenter -> mouseleave incorrectly hides
    // the tooltip while it still has keyboard focus.
    let isFocused = false;
    let isHovered = false;

    const applyVisibility = () => {
        const visible = isFocused || isHovered;
        for (const tooltip of getTooltips(anchorId)) {
            tooltip.visible = visible;
            if (visible) {
                tooltip.setAttribute("visible", "");
            } else {
                tooltip.removeAttribute("visible");
            }
        }
    };
    const onFocus = () => {
        isFocused = true;
        applyVisibility();
    };
    const onBlur = () => {
        isFocused = false;
        applyVisibility();
    };
    const onMouseEnter = () => {
        isHovered = true;
        applyVisibility();
    };
    const onMouseLeave = () => {
        isHovered = false;
        applyVisibility();
    };
    const onKeyDown = event => {
        if (event.key === "Escape") {
            isFocused = false;
            isHovered = false;
            applyVisibility();
        }
    };

    const focusTargets = [anchor, ...getShadowFocusTargets(anchor)];
    for (const target of focusTargets) {
        target.addEventListener("focusin", onFocus);
        target.addEventListener("focusout", onBlur);
        target.addEventListener("focus", onFocus);
        target.addEventListener("blur", onBlur);
        target.addEventListener("mouseenter", onMouseEnter);
        target.addEventListener("mouseleave", onMouseLeave);
        target.addEventListener("keydown", onKeyDown);
    }

    anchor.__aspireTooltip = { onFocus, onBlur, onMouseEnter, onMouseLeave, onKeyDown, focusTargets };

    if (anchor.matches(":focus, :focus-within") || !!anchor.shadowRoot?.activeElement) {
        isFocused = true;
    }
    if (anchor.matches(":hover")) {
        isHovered = true;
    }
    if (isFocused || isHovered) {
        applyVisibility();
    }

    return true;
}

export function disposeAspireTooltip(anchorId) {
    const anchor = document.getElementById(anchorId);
    const tooltip = anchor?.__aspireTooltip;
    if (!anchor || !tooltip) {
        return;
    }

    for (const target of tooltip.focusTargets ?? [anchor]) {
        target.removeEventListener("focusin", tooltip.onFocus);
        target.removeEventListener("focusout", tooltip.onBlur);
        target.removeEventListener("focus", tooltip.onFocus);
        target.removeEventListener("blur", tooltip.onBlur);
        target.removeEventListener("mouseenter", tooltip.onMouseEnter);
        target.removeEventListener("mouseleave", tooltip.onMouseLeave);
        target.removeEventListener("keydown", tooltip.onKeyDown);
    }

    delete anchor.__aspireTooltip;
}

function getShadowFocusTargets(anchor) {
    if (!anchor.shadowRoot) {
        return [];
    }

    return Array.from(anchor.shadowRoot.querySelectorAll("button, a[href], input, select, textarea, [tabindex]"));
}

function getTooltips(anchorId) {
    // Select the anchor's tooltip(s) directly instead of scanning every fluent-tooltip in
    // the document and filtering in JS; anchorId is escaped since it can contain characters
    // that are meaningful in CSS attribute selectors.
    return Array.from(document.querySelectorAll(`fluent-tooltip[anchor="${CSS.escape(anchorId)}"]`));
}
