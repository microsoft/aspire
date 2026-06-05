const interactiveSelector = [
    'a[href]',
    'button',
    'input',
    'select',
    'textarea',
    '[role="button"]',
    '[role="link"]',
    '[role="menuitem"]',
    'fluent-anchor',
    'fluent-button',
    'fluent-checkbox',
    'fluent-combobox',
    'fluent-menu-item',
    'fluent-option',
    'fluent-radio',
    'fluent-search',
    'fluent-select',
    'fluent-switch',
    'fluent-text-field'
].join(',');

export function shouldStopResourcesGridRowKeydown(event) {
    return event.key === 'Enter' && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey;
}

export function initializeResourcesGridKeyboardActivation(grid) {
    if (!grid) {
        return {
            dispose() {
            }
        };
    }

    let registeredElement;

    const onKeyDown = event => {
        // The data grid also treats Enter as row activation. Stop only the keys
        // that activate focused controls so Tab, arrows, Escape, and shortcuts
        // keep bubbling through the grid.
        if (shouldStopResourcesGridRowKeydown(event)) {
            event.stopPropagation();
        }
    };

    const unregisterElement = () => {
        if (registeredElement) {
            registeredElement.removeEventListener('keydown', onKeyDown);
            registeredElement = null;
        }
    };

    const registerElement = element => {
        if (registeredElement === element) {
            return;
        }

        unregisterElement();

        if (element) {
            element.addEventListener('keydown', onKeyDown);
            registeredElement = element;
        }
    };

    const onFocusIn = event => {
        registerElement(getResourcesGridInteractiveElement(grid, event));
    };

    grid.addEventListener('focusin', onFocusIn);

    if (typeof document !== 'undefined') {
        registerElement(getResourcesGridInteractiveElement(grid, { target: document.activeElement }));
    }

    return {
        dispose() {
            grid.removeEventListener('focusin', onFocusIn);
            unregisterElement();
        }
    };
}

function getResourcesGridInteractiveElement(grid, event) {
    const path = typeof event.composedPath === 'function'
        ? event.composedPath()
        : [event.target];

    for (const target of path) {
        if (!(target instanceof Element)) {
            continue;
        }

        const interactiveElement = target.closest(interactiveSelector);
        if (interactiveElement && grid.contains(interactiveElement)) {
            return interactiveElement;
        }
    }

    return null;
}
