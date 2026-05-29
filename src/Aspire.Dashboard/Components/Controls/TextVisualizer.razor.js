import hljs from '/js/highlight-11.11.1.min.js'

function createObserver() {
    let highlightObserver = new MutationObserver((mutations) => {
        mutations.forEach((mutation) => {
            // If the data-content attribute changes, the content for this line's span has changed and so
            // we need to re-highlight it.
            if (mutation.attributeName === "data-content") {
                const target = mutation.target;
                const text = target.getAttribute("data-content");
                const language = target.getAttribute("data-language");
                target.innerHTML = hljs.highlight(language, text).value;
            }

            // On initial open, it's possible that the Virtualize component renders elements after its initial render. There is no hook
            // to know when this happens, so we need to observe the DOM for changes and highlight any new elements that are added.
            if (mutation.addedNodes.length > 0) {
                for (let i = 0; i < mutation.addedNodes.length; i++) {
                    let node = mutation.addedNodes[i];
                    if (node.classList && node.classList.contains("highlight-line")) {
                        hljs.highlightElement(node);
                    }
                }
            }
        })
    });

    return highlightObserver;
}

export function connectObserver(container) {
    if (!container) {
        return;
    }

    // It's possible either that
    // 1. The elements in the log container have already been rendered by the time this method is called, in which
    // case we need to highlight them immediately, or
    // 2. The elements in the log container have not been rendered yet, in which case we need to observe the container
    // for new elements that are added.
    if (container.highlightObserver) {
        container.highlightObserver.disconnect();
    }

    const existingElementsToHighlight = container.getElementsByClassName("highlight-line");
    for (let i = 0; i < existingElementsToHighlight.length; i++) {
        hljs.highlightElement(existingElementsToHighlight[i]);
    }

    var highlightObserver = createObserver();
    highlightObserver.observe(container, {
        childList: true,
        subtree: true,
        attributes: true
    });
    container.highlightObserver = highlightObserver;
}

export function disconnectObserver(container) {
    if (!container) {
        return;
    }

    var highlightObserver = container.highlightObserver;
    if (!highlightObserver) {
        return;
    }

    highlightObserver.disconnect();
    container.highlightObserver = null;
}
