import hljs from '/js/highlight-11.11.1.min.js'

function highlightLine(element) {
    const text = element.getAttribute("data-content");
    const language = element.getAttribute("data-language");
    element.innerHTML = hljs.highlight(text, { language }).value;
    element.classList.add("hljs");
}

function createObserver() {
    let highlightObserver = new MutationObserver((mutations) => {
        mutations.forEach((mutation) => {
            // A format change can reuse the same span without changing its content.
            if ((mutation.attributeName === "data-content" || mutation.attributeName === "data-language") &&
                mutation.target.classList.contains("highlight-line")) {
                highlightLine(mutation.target);
            }

            // On initial open, it's possible that the Virtualize component renders elements after its initial render. There is no hook
            // to know when this happens, so we need to observe the DOM for changes and highlight any new elements that are added.
            if (mutation.addedNodes.length > 0) {
                for (let i = 0; i < mutation.addedNodes.length; i++) {
                    let node = mutation.addedNodes[i];
                    if (node.classList && node.classList.contains("highlight-line")) {
                        highlightLine(node);
                    }
                    if (node.querySelectorAll) {
                        node.querySelectorAll(".highlight-line").forEach(highlightLine);
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
        highlightLine(existingElementsToHighlight[i]);
    }

    var highlightObserver = createObserver();
    highlightObserver.observe(container, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["data-content", "data-language"]
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
