
// To avoid Flash of Unstyled Content, the body is hidden by default with
// the before-upgrade CSS class. Here we'll find the first web component
// and wait for it to be upgraded. When it is, we'll remove that class
// from the body.
const firstUndefinedElement = document.body.querySelector(":not(:defined)");

if (firstUndefinedElement) {
    customElements.whenDefined(firstUndefinedElement.localName).then(() => {
        document.body.classList.remove("before-upgrade");
    });
} else {
    // In the event this code doesn't run until after they've all been upgraded
    document.body.classList.remove("before-upgrade");
}

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
        .catch(() => {
            if (tooltipDiv) {
                tooltipDiv.innerText = 'Could not access clipboard';
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
    const currentElement = document.activeElement;
    const tagName = currentElement.tagName.toLowerCase();

    // fluent components may have shadow roots that contain inputs
    return tagName === "input" || tagName === "textarea" || tagName.startsWith("fluent") ? isInputElement(currentElement, false) : false;
}

function isInputElement(element, isRoot, isShadowRoot) {
    const tag = element.tagName.toLowerCase();
    // comes from https://developer.mozilla.org/en-US/docs/Web/API/Element/input_event
    // fluent-select does not use <select /> element
    if (tag === "input" || tag === "textarea" || tag === "select" || tag === "fluent-select") {
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

const aspirePopupKeyboardNavigationState = new Map();

window.initializeAspirePopupKeyboardNavigation = function (anchorId, popupId, dotNetHelper, options) {
    window.disposeAspirePopupKeyboardNavigation(anchorId, popupId);

    const anchorElement = document.getElementById(anchorId);
    if (!anchorElement) {
        return;
    }

    const key = getAspirePopupKeyboardNavigationKey(anchorId, popupId);
    const tabExitsAlways = options?.tabExitsAlways ?? options?.TabExitsAlways ?? false;
    const resolvePopupElement = () => document.getElementById(popupId);

    const popupKeydownListener = function (ev) {
        const isEscape = ev.key === "Escape" || ev.keyCode === 27;
        if (ev.key !== "Tab" && !isEscape) {
            return;
        }

        if (isEscape) {
            const popupElement = resolvePopupElement();
            const expandedSubmenuItem = ev.target instanceof Element
                ? ev.target.closest("fluent-menu-item[aria-expanded='true']")
                : null;
            if (popupElement?.contains(expandedSubmenuItem)) {
                // Fluent's Escape handler closes the entire menu tree. Convert it to the
                // native submenu-collapse key so Fluent retains ownership of submenu state.
                stopPopupKeyboardEvent(ev);
                expandedSubmenuItem.dispatchEvent(new KeyboardEvent("keydown", {
                    key: "ArrowLeft",
                    code: "ArrowLeft",
                    keyCode: 37,
                    bubbles: true,
                    composed: true
                }));
                return;
            }

            stopPopupKeyboardEvent(ev);
            anchorElement.focus();
            dotNetHelper.invokeMethodAsync("CloseAsync");
            return;
        }

        if (tabExitsAlways) {
            const popupElement = resolvePopupElement();
            stopPopupKeyboardEvent(ev);
            if (ev.shiftKey) {
                anchorElement.focus();
            } else {
                focusNextElementAfterAnchor(anchorElement, popupElement);
            }
            dotNetHelper.invokeMethodAsync("CloseAsync");
            return;
        }

        const popupElement = resolvePopupElement();
        if (!popupElement) {
            return;
        }

        const focusableElements = getAspireFocusableElements(popupElement);
        const activeIndex = findAspireActiveElementIndex(focusableElements);

        if (!ev.shiftKey && (focusableElements.length === 0 || activeIndex === focusableElements.length - 1)) {
            stopPopupKeyboardEvent(ev);
            focusNextElementAfterAnchor(anchorElement, popupElement);
            dotNetHelper.invokeMethodAsync("CloseAsync");
        } else if (ev.shiftKey && (focusableElements.length === 0 || activeIndex === 0)) {
            stopPopupKeyboardEvent(ev);
            anchorElement.focus();
            dotNetHelper.invokeMethodAsync("CloseAsync");
        }
    };

    const anchorKeydownListener = function (ev) {
        const isEscape = ev.key === "Escape" || ev.keyCode === 27;
        if (ev.key !== "Tab" && !isEscape) {
            return;
        }

        if (isEscape) {
            stopPopupKeyboardEvent(ev);
            anchorElement.focus();
            dotNetHelper.invokeMethodAsync("CloseAsync");
            return;
        }

        if (ev.shiftKey) {
            // Shift+Tab on the anchor: let the browser move focus back to the previous
            // page element and close the popup. We intentionally do NOT preventDefault
            // here (the native focus shift is the desired behavior), but we still stop
            // propagation so Fluent UI's own keyboard helper doesn't also fire and
            // calculate the previous element from the wrong (shadow DOM) starting point.
            ev.stopPropagation();
            ev.stopImmediatePropagation();
            dotNetHelper.invokeMethodAsync("CloseAsync");
            return;
        }

        const popupElement = resolvePopupElement();
        if (!popupElement) {
            return;
        }

        const firstFocusable = getAspireFocusableElements(popupElement)[0];
        if (firstFocusable) {
            stopPopupKeyboardEvent(ev);
            firstFocusable.focus();
        } else {
            stopPopupKeyboardEvent(ev);
            focusNextElementAfterAnchor(anchorElement, popupElement);
            dotNetHelper.invokeMethodAsync("CloseAsync");
        }
    };

    const documentKeydownListener = function (ev) {
        const isEscape = ev.key === "Escape" || ev.keyCode === 27;
        if (ev.key !== "Tab" && !isEscape) {
            return;
        }

        const eventPath = typeof ev.composedPath === "function" ? ev.composedPath() : [];
        const popupElement = resolvePopupElement();
        const isFromAnchor = eventPath.includes(anchorElement) || anchorElement.contains(ev.target);
        const isFromPopup = popupElement && (eventPath.includes(popupElement) || popupElement.contains(ev.target));

        if (isFromAnchor) {
            anchorKeydownListener(ev);
        } else if (isFromPopup) {
            popupKeydownListener(ev);
        }
    };

    // Fluent UI's popup keyboard helper currently calculates the next page element from
    // the inner shadow DOM target for fluent-button anchors and menu items. Those inner
    // elements are not in document order, so Tab can wrap to the first focusable control
    // on the page. Capture Tab at the document before Fluent UI's listener and calculate
    // from the stable host elements instead.
    document.addEventListener("keydown", documentKeydownListener, true);

    aspirePopupKeyboardNavigationState.set(key, {
        anchorElement,
        documentKeydownListener
    });
};

window.disposeAspirePopupKeyboardNavigation = function (anchorId, popupId) {
    const key = getAspirePopupKeyboardNavigationKey(anchorId, popupId);
    const state = aspirePopupKeyboardNavigationState.get(key);
    if (!state) {
        return;
    }

    document.removeEventListener("keydown", state.documentKeydownListener, true);
    aspirePopupKeyboardNavigationState.delete(key);
};

function getAspirePopupKeyboardNavigationKey(anchorId, popupId) {
    return `${anchorId}:${popupId}`;
}

function stopPopupKeyboardEvent(ev) {
    ev.preventDefault();
    ev.stopPropagation();
    ev.stopImmediatePropagation();
}

function getAspireFocusableElements(container, excludedContainer) {
    const focusableSelector = "input, select, textarea, button, object, a[href], area[href], iframe, summary, [tabindex], [contenteditable='true']";
    const focusableElements = [];

    for (const element of container.querySelectorAll("*")) {
        if (excludedContainer?.contains(element)) {
            continue;
        }

        if (isAspireFocusableElement(element, focusableSelector)) {
            focusableElements.push(element);
        }
    }

    return focusableElements;
}

function isAspireFocusableElement(element, focusableSelector) {
    const tagName = element.tagName.toLowerCase();
    const isFluentInteractiveElement = tagName === "fluent-anchor"
        || tagName === "fluent-button"
        || tagName === "fluent-checkbox"
        || tagName === "fluent-menu-item"
        || tagName === "fluent-radio"
        || tagName === "fluent-search"
        || tagName === "fluent-select"
        || tagName === "fluent-switch"
        || tagName === "fluent-tab"
        || tagName === "fluent-text-field";

    if (!isFluentInteractiveElement && !element.matches(focusableSelector)) {
        return false;
    }

    // A negative tabIndex always opts an element out of sequential focus navigation,
    // including Fluent custom elements. Without this check, a fluent-button with
    // tabindex="-1" (e.g. an item that's only programmatically focused) would still
    // be picked up by Tab navigation through the popup.
    if (element.tabIndex < 0) {
        return false;
    }

    if (element.disabled || element.getAttribute("aria-disabled") === "true") {
        return false;
    }

    return isAspireElementVisible(element);
}

function isAspireElementVisible(element) {
    if (typeof element.checkVisibility === "function") {
        return element.checkVisibility();
    }

    return !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length);
}

function findAspireActiveElementIndex(focusableElements) {
    const activeElement = document.activeElement;
    const deepActiveElement = getAspireDeepActiveElement();

    return focusableElements.findIndex(element =>
        element === activeElement
        || element === deepActiveElement
        || element.contains(deepActiveElement)
        || element.shadowRoot?.contains(deepActiveElement));
}

function getAspireDeepActiveElement() {
    let activeElement = document.activeElement;
    while (activeElement?.shadowRoot?.activeElement) {
        activeElement = activeElement.shadowRoot.activeElement;
    }

    return activeElement;
}

function focusNextElementAfterAnchor(anchorElement, popupElement) {
    const root = anchorElement.getRootNode() instanceof Document
        ? anchorElement.getRootNode().body
        : document.body;
    const focusableSelector = "input, select, textarea, button, object, a[href], area[href], iframe, summary, [tabindex], [contenteditable='true']";

    // Walk the document in source order and stop at the first focusable element that
    // comes after the anchor. Building the full focusable list (the previous approach)
    // is wasteful when the page has many controls and the answer is usually a sibling
    // of the anchor a few nodes away. Elements inside the popup are skipped because Tab
    // is supposed to land *after* the popup, not back inside it.
    let foundAnchor = false;
    for (const element of root.querySelectorAll("*")) {
        if (popupElement?.contains(element)) {
            continue;
        }

        if (!foundAnchor) {
            if (element === anchorElement) {
                foundAnchor = true;
            }
            continue;
        }

        if (isAspireFocusableElement(element, focusableSelector)) {
            element.focus();
            return;
        }
    }

    // No focusable element follows the anchor. Keep focus on the anchor so the user
    // doesn't get warped to the top of the page.
    anchorElement.focus();
}

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
