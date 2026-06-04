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
    return event.key === 'Enter';
}

export function initializeResourcesGridKeyboardActivation(grid) {
    if (!grid) {
        return {
            dispose() {
            }
        };
    }

    const registeredElements = new Set();

    const onKeyDown = event => {
        // The data grid also treats Enter as row activation. Stop only the keys
        // that activate focused controls so Tab, arrows, Escape, and shortcuts
        // keep bubbling through the grid.
        if (shouldStopResourcesGridRowKeydown(event)) {
            event.stopPropagation();
        }
    };

    const registerElement = element => {
        if (!registeredElements.has(element)) {
            element.addEventListener('keydown', onKeyDown);
            registeredElements.add(element);
        }
    };

    const unregisterElement = element => {
        if (registeredElements.delete(element)) {
            element.removeEventListener('keydown', onKeyDown);
        }
    };

    const registerTree = node => {
        if (node.nodeType !== Node.ELEMENT_NODE) {
            return;
        }

        if (node.matches(interactiveSelector)) {
            registerElement(node);
        }

        for (const element of node.querySelectorAll(interactiveSelector)) {
            registerElement(element);
        }
    };

    const unregisterTree = node => {
        if (node.nodeType !== Node.ELEMENT_NODE) {
            return;
        }

        if (node.matches(interactiveSelector)) {
            unregisterElement(node);
        }

        for (const element of node.querySelectorAll(interactiveSelector)) {
            unregisterElement(element);
        }
    };

    registerTree(grid);

    const observer = new MutationObserver(mutations => {
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                registerTree(node);
            }

            for (const node of mutation.removedNodes) {
                unregisterTree(node);
            }
        }
    });
    observer.observe(grid, { childList: true, subtree: true });

    return {
        dispose() {
            observer.disconnect();

            for (const element of registeredElements) {
                element.removeEventListener('keydown', onKeyDown);
            }

            registeredElements.clear();
        }
    };
}
