export function initializeAspireTooltip(anchorId) {
    disposeAspireTooltip(anchorId);

    const anchor = document.getElementById(anchorId);
    if (!anchor) {
        return false;
    }

    const show = () => {
        for (const tooltip of getTooltips(anchorId)) {
            tooltip.visible = true;
            tooltip.setAttribute("visible", "");
        }
    };
    const hide = () => {
        for (const tooltip of getTooltips(anchorId)) {
            tooltip.visible = false;
            tooltip.removeAttribute("visible");
        }
    };
    const onKeyDown = event => {
        if (event.key === "Escape") {
            hide();
        }
    };

    const focusTargets = [anchor, ...getShadowFocusTargets(anchor)];
    for (const target of focusTargets) {
        target.addEventListener("focusin", show);
        target.addEventListener("focusout", hide);
        target.addEventListener("focus", show);
        target.addEventListener("blur", hide);
        target.addEventListener("mouseenter", show);
        target.addEventListener("mouseleave", hide);
        target.addEventListener("keydown", onKeyDown);
    }

    anchor.__aspireTooltip = { show, hide, onKeyDown, focusTargets };

    if (hasActiveInteraction(anchor)) {
        show();
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
        target.removeEventListener("focusin", tooltip.show);
        target.removeEventListener("focusout", tooltip.hide);
        target.removeEventListener("focus", tooltip.show);
        target.removeEventListener("blur", tooltip.hide);
        target.removeEventListener("mouseenter", tooltip.show);
        target.removeEventListener("mouseleave", tooltip.hide);
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
    return Array.from(document.querySelectorAll("fluent-tooltip"))
        .filter(tooltip => tooltip.getAttribute("anchor") === anchorId);
}

function hasActiveInteraction(anchor) {
    return anchor.matches(":focus, :focus-within, :hover") || !!anchor.shadowRoot?.activeElement;
}
