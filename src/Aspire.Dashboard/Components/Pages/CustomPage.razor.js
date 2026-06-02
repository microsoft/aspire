export function addStylesheetLink(href) {
    // Avoid duplicates.
    if (document.querySelector(`link[href="${href}"]`)) {
        return;
    }
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    document.head.appendChild(link);
}

export function removeStylesheetLink(href) {
    const link = document.querySelector(`link[href="${href}"]`);
    if (link) {
        link.remove();
    }
}

export async function invokeOptionalExport(module, functionName) {
    const exportedFunction = module?.[functionName];
    if (typeof exportedFunction === 'function') {
        await exportedFunction();
    }
}

export function attachButtonClickEvent(containerId, interop) {
    const container = document.getElementById(containerId);
    if (!container) {
        console.log(`Couldn't find container '${containerId}'.`);
        return;
    }

    container.addEventListener('click', function (event) {
        const button = event.target.closest('fluent-button[data-action]') || event.target.closest('a[data-action]');
        if (button) {
            event.preventDefault();
            event.stopPropagation();

            // Collect all data-* attributes into a dictionary to pass to Blazor.
            const prefix = 'data-';
            const prefixLength = prefix.length;

            const values = {};
            for (const attr of button.attributes) {
                if (attr.name.startsWith(prefix)) {
                    values[attr.name.substring(prefixLength)] = attr.value;
                }
            }

            interop.invokeMethodAsync('OnButtonClick', values);
        }
    });
}


