// Fluent menu items invoke their primary action for activation events originating anywhere in the
// item, including interactive content in the end slot. Customize the registered element class so
// every instance leaves secondary-action activation to the nested button.
const customElementsDefine = customElements.define.bind(customElements);
const fluentDropdownStyleSheet = new CSSStyleSheet();
fluentDropdownStyleSheet.replaceSync(`
    .control {
        background-color: var(--colorNeutralBackground1);
        border: 1px solid var(--colorNeutralStroke1);
        box-shadow: none !important;
    }

    .control:hover {
        border-color: var(--colorNeutralStroke1Hover);
    }

    .control:active {
        border-color: var(--colorNeutralStroke1Pressed);
    }

    :host(:where(:focus-within)) .control {
        outline: 2px solid var(--colorBrandStroke1);
        outline-offset: -2px;
    }

    .control::before, .control::after {
        display: none;
    }
`);

let isDropdownCustomized = false;
let isMenuItemCustomized = false;

customElements.define = function (name, constructor, options) {
    if (name === "fluent-dropdown") {
        const connectedCallback = constructor.prototype.connectedCallback;

        constructor.prototype.connectedCallback = function () {
            connectedCallback.call(this);

            // Fluent v5 doesn't expose the dropdown control as a CSS part, so add the
            // application's border recipe directly to the component's shadow root.
            if (!this.shadowRoot.adoptedStyleSheets.includes(fluentDropdownStyleSheet)) {
                this.shadowRoot.adoptedStyleSheets.push(fluentDropdownStyleSheet);
            }
        };

        isDropdownCustomized = true;
    }

    if (name === "fluent-menu-item") {
        const secondaryActionCustomized = Symbol("secondaryActionCustomized");
        const connectedCallback = constructor.prototype.connectedCallback;

        constructor.prototype.connectedCallback = function () {
            if (!this[secondaryActionCustomized]) {
                for (const handlerName of ["handleMenuItemClick", "handleMenuItemKeyDown"]) {
                    const handler = this[handlerName];
                    this[handlerName] = event => {
                        const isSecondaryAction = event.composedPath().some(element =>
                            element instanceof HTMLElement &&
                            element.classList.contains("aspire-menu-secondary-action"));

                        if (isSecondaryAction) {
                            if (handlerName === "handleMenuItemKeyDown") {
                                event.stopPropagation();
                            }

                            return false;
                        }

                        return handler.call(this, event);
                    };
                }

                this[secondaryActionCustomized] = true;
            }

            connectedCallback.call(this);
        };

        isMenuItemCustomized = true;
    }

    customElementsDefine(name, constructor, options);

    if (isDropdownCustomized && isMenuItemCustomized) {
        delete customElements.define;
    }
};

function isElementTagName(element, tagName) {
    return element.tagName.toLowerCase() === tagName;
}

function getFluentMenuItemForTarget(element) {
    // User could have clicked on either a path or svg (the image on the item) or the item itself
    if (isElementTagName(element, "path")) {
        return getFluentMenuItemForTarget(element.parentElement);
    }

    // in between the svg and fluent-menu-item is a span for the icon slot
    const possibleMenuItem = element.parentElement?.parentElement;
    if (possibleMenuItem && (isElementTagName(possibleMenuItem, "fluent-menu-item") || isElementTagName(possibleMenuItem, "button"))) {
        return element.parentElement.parentElement;
    }

    if (isElementTagName(element, "fluent-menu-item") || isElementTagName(element, "button")) {
        return element;
    }

    return null;
}

// File inputs bubble cancel when the picker is dismissed (or the same file is selected).
// Stop it before Fluent's shadow-DOM dialog handler treats it as a dialog cancellation.
// Native dialog cancel events, including Escape, must still reach Fluent.
// https://developer.mozilla.org/en-US/docs/Web/API/HTMLInputElement/cancel_event
document.addEventListener("cancel", function (event) {
    if (event.target instanceof HTMLInputElement && event.target.type === "file" &&
        event.target.closest("fluent-dialog, fluent-drawer")) {
        event.stopPropagation();
    }
}, true);

// Register a global click event listener to handle copy/open button clicks.
// Required because an "onclick" attribute is denied by CSP.
document.addEventListener("click", function (e) {
    // The copy 'button' could either be a button or a menu item.
    const targetElement = isElementTagName(e.target, "fluent-button") ? e.target : getFluentMenuItemForTarget(e.target);
    if (targetElement) {
        if (targetElement.getAttribute("data-copybutton")) {
            buttonCopyTextToClipboard(targetElement);
        } else if (targetElement.getAttribute("data-openbutton")) {
            buttonOpenLink(targetElement);
        }
        e.stopPropagation();
    }
});

let isScrolledToContent = false;
let lastScrollHeight = null;

window.getIsScrolledToContent = function () {
    return isScrolledToContent;
}

window.setIsScrolledToContent = function (value) {
    if (isScrolledToContent != value) {
        isScrolledToContent = value;
        console.log(`isScrolledToContent=${isScrolledToContent}`);
    }
}

window.resetContinuousScrollPosition = function () {
    // Reset to scrolling to the end of the content after switching.
    setIsScrolledToContent(false);
}

window.initializeContinuousScroll = function () {
    // Reset to scrolling to the end of the content when initializing.
    // This needs to be called because the value is remembered across Aspire pages because the browser isn't reloading.
    resetContinuousScrollPosition();

    const container = document.querySelector('.continuous-scroll-overflow');
    if (container == null) {
        return;
    }

    // The scroll event is used to detect when the user scrolls to view content.
    container.addEventListener('scroll', () => {
        var atBottom = isScrolledToBottom(container);
        if (atBottom === null) {
            return;
        }
        setIsScrolledToContent(!atBottom);
   }, { passive: true });

    // The ResizeObserver reports changes in the grid size.
    // This ensures that the logs are scrolled to the bottom when there are new logs
    // unless the user has scrolled to view content.
    const observer = new ResizeObserver(function () {
        lastScrollHeight = container.scrollHeight;

        if (lastScrollHeight == container.clientHeight) {
            // There is no scrollbar. This could be because there's no content, or the content might have been cleared.
            // Reset to default behavior: scroll to bottom
            setIsScrolledToContent(false);
            return;
        }

        var isScrolledToContent = getIsScrolledToContent();
        if (!isScrolledToContent) {
            container.scrollTop = lastScrollHeight;
            return;
        }
    });
    for (const child of container.children) {
        observer.observe(child);
    }
};

function isScrolledToBottom(container) {
    lastScrollHeight = lastScrollHeight || container.scrollHeight

    // There can be a race between resizing and scrolling events.
    // Use the last scroll height from the resize event to figure out if we've scrolled to the bottom.
    if (!getIsScrolledToContent()) {
        if (lastScrollHeight != container.scrollHeight) {
            console.log(`lastScrollHeight ${lastScrollHeight} doesn't equal container scrollHeight ${container.scrollHeight}.`);

            // Unknown because the container size changed.
            return null;
        }
    }

    const marginOfError = 5;
    const containerScrollBottom = lastScrollHeight - container.clientHeight;
    const difference = containerScrollBottom - container.scrollTop;

    var atBottom = difference < marginOfError;
    return atBottom;
}

window.buttonOpenLink = function (element) {
    const url = element.getAttribute("data-url");
    const target = element.getAttribute("data-target");

    window.open(url, target, "noopener,noreferrer");
}

window.buttonCopyTextToClipboard = function(element) {
    const text = element.getAttribute("data-text");
    const precopy = element.getAttribute("data-precopy");
    const postcopy = element.getAttribute("data-postcopy");

    copyTextToClipboard(element.getAttribute("id"), text, precopy, postcopy);
}

window.copyTextToClipboard = function (id, text, precopy, postcopy) {
    const button = document.getElementById(id);

    // If there is a pending timeout then clear it. Otherwise the pending timeout will prematurely reset values.
    if (button.dataset.copyTimeout) {
        clearTimeout(button.dataset.copyTimeout);
        delete button.dataset.copyTimeout;
    }

    const copyIcon = button.querySelector('.copy-icon');
    const checkmarkIcon = button.querySelector('.checkmark-icon');

    const anchoredTooltip = document.querySelector(`fluent-tooltip[anchor="${id}"]`);
    const tooltipDiv = anchoredTooltip ? anchoredTooltip.children[0] : null;
    navigator.clipboard.writeText(text)
        .then(() => {
            if (tooltipDiv) {
                tooltipDiv.innerText = postcopy;
            }
            if (copyIcon && checkmarkIcon) {
                copyIcon.style.display = 'none';
                checkmarkIcon.style.display = '';
            }
        })
        .catch(error => {
            if (tooltipDiv) {
                tooltipDiv.innerText = 'Could not access clipboard';
            } else {
                console.warn("Dashboard clipboard copy failed.", error);
            }
        });

    button.dataset.copyTimeout = setTimeout(function () {
        if (tooltipDiv) {
            tooltipDiv.innerText = precopy;
        }

        if (copyIcon && checkmarkIcon) {
            copyIcon.style.display = '';
            checkmarkIcon.style.display = 'none';
        }
        delete button.dataset.copyTimeout;
    }, 1500);
};

window.copyText = function (text) {
    return navigator.clipboard.writeText(text);
};

function isActiveElementInput() {
    let currentElement = document.activeElement;
    // Document.activeElement is the shadow host when Hex1b's textarea has
    // focus. Follow focused shadow roots so printable keys remain terminal
    // input rather than triggering dashboard navigation shortcuts. Stop at
    // Fluent dropdowns so their host-level input semantics are preserved.
    // https://developer.mozilla.org/en-US/docs/Web/API/Document/activeElement
    while (currentElement.shadowRoot?.activeElement && !currentElement.closest("fluent-dropdown")) {
        currentElement = currentElement.shadowRoot.activeElement;
    }

    // Fluent v5 renders the dropdown's focusable control as a light-DOM button. Treat the control
    // and popup options as input elements so global shortcuts don't run while a dropdown is active.
    if (currentElement.closest("fluent-dropdown")) {
        return true;
    }

    const tagName = currentElement.tagName.toLowerCase();

    // fluent components may have shadow roots that contain inputs
    return tagName === "input" || tagName === "textarea" || tagName.startsWith("fluent") ? isInputElement(currentElement, false) : false;
}

function isInputElement(element, isRoot, isShadowRoot) {
    const tag = element.tagName.toLowerCase();
    // comes from https://developer.mozilla.org/en-US/docs/Web/API/Element/input_event
    if (tag === "input" || tag === "textarea" || tag === "select" || tag === "fluent-dropdown") {
        return true;
    }

    if (isShadowRoot || isRoot) {
        const elementChildren = element.children;
        for (let i = 0; i < elementChildren.length; i++) {
            if (isInputElement(elementChildren[i], false, isShadowRoot)) {
                return true;
            }
        }
    }

    const shadowRoot = element.shadowRoot;
    if (shadowRoot) {
        const shadowRootChildren = shadowRoot.children;
        for (let i = 0; i < shadowRootChildren.length; i++) {
            if (isInputElement(shadowRootChildren[i], false, true)) {
                return true;
            }
        }
    }

    return false;
}

window.registerGlobalKeydownListener = function (shortcutManager) {
    function hasNoModifiers(keyboardEvent) {
        return !keyboardEvent.altKey && !keyboardEvent.ctrlKey && !keyboardEvent.metaKey && !keyboardEvent.shiftKey;
    }

    // Shift in some but not all, keyboard layouts, is used for + and -
    function modifierKeysExceptShiftNotPressed(keyboardEvent) {
        return !keyboardEvent.altKey && !keyboardEvent.ctrlKey && !keyboardEvent.metaKey;
    }

    function calculateShortcut(e) {
        if (modifierKeysExceptShiftNotPressed(e)) {
            /* general shortcuts */
            switch (e.key) {
                case "?": // help
                    return 100;
                case "S": // settings
                    return 110;

                /* panel shortcuts */
                case "T": // toggle panel orientation
                    return 300;
                case "X": // close panel
                    return 310;
                case "R": // reset panel sizes
                    return 320;
                case "+": // increase panel size
                    return 330;
                case "_": // decrease panel size
                case "-":
                    return 340;
            }
        }

        if (hasNoModifiers(e)) {
            // Match the unmodified physical Backquote key across keyboard layouts, not the produced character.
            // The focused-input guard runs before this, so terminal and text inputs still receive their keys.
            // To toggle from terminal input, press F6 first to focus its controls.
            // https://developer.mozilla.org/en-US/docs/Web/API/KeyboardEvent/code
            if (e.code === "Backquote") {
                return 400;
            }

            switch (e.key) {
                case "r": // go to resources
                    return 200;
                case "c": // go to console logs
                    return 210;
                case "s": // go to structured logs
                    return 220;
                case "t": // go to traces
                    return 230;
                case "m": // go to metrics
                    return 240;
            }
        }

        return null;
    }

    const keydownListener = function (e) {
        if (isActiveElementInput()) {
            return;
        }

        // list of shortcut enum codes is in src/Aspire.Dashboard/Model/IGlobalKeydownListener.cs
        // to serialize an enum from js->dotnet, we must pass the enum's integer value, not its name
        let shortcut = calculateShortcut(e);

        if (shortcut) {
            shortcutManager.invokeMethodAsync('OnGlobalKeyDown', shortcut);
        }
    }

    window.document.addEventListener('keydown', keydownListener);

    return {
        keydownListener: keydownListener,
    }
};

window.unregisterGlobalKeydownListener = function (obj) {
    window.document.removeEventListener('keydown', obj.keydownListener);
};

window.getBrowserInfo = function () {
    const options = Intl.DateTimeFormat(undefined, { hour: 'numeric' }).resolvedOptions();

    return {
        timeZone: options.timeZone,
        userAgent: navigator.userAgent,
        is24HourTime: options.hourCycle === "h23" || options.hourCycle === "h24"
    };
};

window.focusElement = function (selector, suppressFocusVisible) {
    const element = document.getElementById(selector);
    if (element) {
        if (suppressFocusVisible) {
            element.focus({ focusVisible: false });
        } else {
            element.focus();
        }
    }
};

window.initializeMobileNavMenuKeyboardNavigation = function (dotnetHelper, menuId) {
    const menu = document.getElementById(menuId);

    const keydownListener = function (event) {
        if (event.key === "Escape") {
            event.preventDefault();
            dotnetHelper.invokeMethodAsync("CloseMobileNavMenuFromKeyboardAsync");
        }
    };

    const focusoutListener = function (event) {
        if (!menu.contains(event.relatedTarget)) {
            dotnetHelper.invokeMethodAsync("CloseMobileNavMenuFromFocusLossAsync");
        }
    };

    // Keep Escape-to-close available as soon as the menu opens, including while
    // focus is still on the navigation button that opened this inline menu.
    // Do not trap Tab: focusout closes the menu after focus naturally leaves it.
    document.addEventListener("keydown", keydownListener, true);
    menu?.addEventListener("focusout", focusoutListener);

    return {
        keydownListener,
        focusoutListener,
        menu
    };
};

window.disposeMobileNavMenuKeyboardNavigation = function (obj) {
    document.removeEventListener("keydown", obj.keydownListener, true);
    obj.menu?.removeEventListener("focusout", obj.focusoutListener);
};

window.getWindowDimensions = function() {
    return {
        width: window.innerWidth,
        height: window.innerHeight
    };
}

window.listenToWindowResize = function(dotnetHelper) {
    function throttle(func, timeout) {
        let currentTimeout = null;
        return function () {
            if (currentTimeout) {
                return;
            }
            const context = this;
            const args = arguments;
            const later = () => {
                func.call(context, ...args);
                currentTimeout = null;
            }
            currentTimeout = setTimeout(later, timeout);
        }
    }

    const throttledResizeListener = throttle(() => {
        dotnetHelper.invokeMethodAsync('OnResizeAsync', { width: window.innerWidth, height: window.innerHeight });
    }, 150)

    window.addEventListener('load', throttledResizeListener);

    window.addEventListener('resize', throttledResizeListener);
}

window.setCellTextClickHandler = function (id) {
    var cellTextElement = document.getElementById(id);
    if (!cellTextElement) {
        return;
    }

    cellTextElement.addEventListener('click', e => {
        // Propagation behavior:
        // - Link click stops. Link will open in a new window.
        // - Any other text allows propagation. Potentially opens details view.
        if (isElementTagName(e.target, 'a')) {
            e.stopPropagation();
        }
    });
};

window.scrollToTop = function (selector) {
    var element = document.querySelector(selector);
    if (element) {
        element.scrollTop = 0;
    }
};

window.scrollToElement = function (elementId) {
    var element = document.getElementById(elementId);
    if (element) {
        element.scrollIntoView({ behavior: 'smooth' });
    }
};

// ===== Data grid column auto-fit =====
// Double-clicking a FluentDataGrid column's resize handle expands (or shrinks) that column so the
// widest visible cell content fits, then animates the change. FluentDataGrid renders as
// <table class="fluent-data-grid"> laid out with display:grid; the column widths live in the
// table's inline grid-template-columns, e.g.:
//   grid-template-columns: 1.5fr 1.25fr 1fr 2.25fr 2.25fr minmax(150px, 1.5fr);
// We measure a column's natural content width by momentarily setting just that track to
// max-content, read the resolved width, then animate the fully-resolved px template from the old
// width to the fitted width.
//
// This is intentionally self-contained (it does not rely on Fluent's internal resize JS) so it
// keeps working across Fluent UI Blazor upgrades, and it's wired as a document-level listener so it
// survives Blazor SPA navigations and applies to every grid (Resources, Console, Structured,
// Traces, Metrics).
const AUTOFIT_ANIMATING_CLASS = "autofit-animating";
const AUTOFIT_CONTENT_PADDING = 8; // a little breathing room past the measured content
const AUTOFIT_MIN_WIDTH = 48;      // never collapse a column to nothing

function autoFitGridColumn(handle) {
    const grid = handle.closest("table.fluent-data-grid");
    const header = handle.closest("th[cell-type='columnheader']");
    if (!grid || !header) {
        return;
    }

    const headers = Array.from(grid.querySelectorAll("th[cell-type='columnheader']"));
    const columnIndex = headers.indexOf(header);
    if (columnIndex < 0) {
        return;
    }

    // Resolve the current tracks to concrete px so we have an explicit, animatable start state.
    // getComputedStyle always returns used px values (fr / minmax resolved), space separated.
    const startTracks = getComputedStyle(grid).gridTemplateColumns.split(" ");
    // Guard against grids whose resolved track count doesn't line up with the header cells (e.g. an
    // extra structural track); bailing avoids corrupting the layout with a misaligned template.
    if (startTracks.length !== headers.length) {
        return;
    }

    // Measure: let only this column grow to its content, read the resulting width, then restore.
    // A grid max-content track sizes to the widest content contribution of the rendered cells,
    // which is exactly "fit to the longest value currently on screen".
    const measureTracks = startTracks.slice();
    measureTracks[columnIndex] = "max-content";
    grid.classList.remove(AUTOFIT_ANIMATING_CLASS);
    grid.style.gridTemplateColumns = measureTracks.join(" ");
    // Force layout so the max-content measurement reflects the real content width.
    void grid.offsetWidth;

    // Cap the fit so one very long value (e.g. a big URL/source) can't swallow the whole grid.
    const maxWidth = Math.max(200, grid.clientWidth * 0.7);
    const measured = header.getBoundingClientRect().width + AUTOFIT_CONTENT_PADDING;
    const fitWidth = Math.min(Math.max(measured, AUTOFIT_MIN_WIDTH), maxWidth);

    // Restore the start widths (still no transition) so the animation begins from the old size.
    grid.style.gridTemplateColumns = startTracks.join(" ");
    void grid.offsetWidth;

    // Animate to the fitted width. Only this one track changes; the others stay pinned to their
    // current px, so the grid grows/shrinks predictably - matching normal drag-resize behavior.
    const targetTracks = startTracks.slice();
    targetTracks[columnIndex] = `${fitWidth.toFixed(2)}px`;
    grid.classList.add(AUTOFIT_ANIMATING_CLASS);
    grid.style.gridTemplateColumns = targetTracks.join(" ");

    const cleanup = function (e) {
        // transitionend fires per animated property; only react to the one we drive.
        if (e && e.propertyName !== "grid-template-columns") {
            return;
        }
        grid.classList.remove(AUTOFIT_ANIMATING_CLASS);
        grid.removeEventListener("transitionend", cleanup);
    };
    grid.addEventListener("transitionend", cleanup);
    // Fallback in case transitionend never fires (no measurable change, reduced motion, or a
    // browser that can't interpolate grid-template-columns and snaps instantly instead).
    setTimeout(cleanup, 500);
}

// Register a global double-click listener for grid resize handles. Fluent UI v5 marks its
// dynamically created handle with actual-resize-handle; keep the v4 class selectors so the
// listener remains compatible with dashboard assets from older Fluent UI versions.
document.addEventListener("dblclick", function (e) {
    const handle = e.target.closest?.(".fluent-data-grid [actual-resize-handle], .fluent-data-grid .resize-handle, .fluent-data-grid .col-width-draghandle");
    if (handle) {
        // Prevent the double-click from selecting the header text while we resize.
        e.preventDefault();
        autoFitGridColumn(handle);
    }
});

// taken from https://learn.microsoft.com/en-us/aspnet/core/blazor/file-downloads?view=aspnetcore-8.0#download-from-a-stream
window.downloadStreamAsFile = async function (fileName, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const anchorElement = document.createElement('a');
    anchorElement.href = url;
    anchorElement.download = fileName ?? '';
    anchorElement.click();
    anchorElement.remove();
    URL.revokeObjectURL(url);
};
