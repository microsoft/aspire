/*
 Reports which instrument cards are near the viewport, so the server only refreshes the sparklines someone can see.
 Cards are identified by their data-instrument-id attribute. The report is debounced: scrolling through hundreds of
 cards would otherwise call the server for every card that crosses the margin.
 */
export function observeCards(container, dotNetReference) {
    const visibleIds = new Set();
    let reportTimer = null;
    let disposed = false;

    const scheduleReport = () => {
        if (reportTimer !== null || disposed) {
            return;
        }

        reportTimer = setTimeout(() => {
            reportTimer = null;
            if (!disposed) {
                dotNetReference.invokeMethodAsync('OnVisibleCardsChanged', [...visibleIds]).catch(() => { });
            }
        }, 150);
    };

    // The root is the viewport, and intersections are clipped by the scrolling content pane. The margin of a screen
    // height keeps the cards just above and below fresh, so scrolling a little shows current values.
    const intersectionObserver = new IntersectionObserver(entries => {
        for (const entry of entries) {
            const id = entry.target.dataset.instrumentId;
            if (entry.isIntersecting) {
                visibleIds.add(id);
            } else {
                visibleIds.delete(id);
            }
        }
        scheduleReport();
    }, { rootMargin: '100% 0px' });

    const observe = card => intersectionObserver.observe(card);
    container.querySelectorAll('.meter-card[data-instrument-id]').forEach(observe);

    // Blazor adds, removes and reorders cards as instruments arrive or the sort order changes. A moved card is removed
    // and inserted again, and observing it again reports where it is now.
    const mutationObserver = new MutationObserver(mutations => {
        let removed = false;
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node instanceof Element && node.matches('.meter-card[data-instrument-id]')) {
                    observe(node);
                }
            }
            for (const node of mutation.removedNodes) {
                if (node instanceof Element && node.matches('.meter-card[data-instrument-id]')) {
                    intersectionObserver.unobserve(node);
                    removed = true;
                }
            }
        }

        if (removed) {
            // A removed card may have been visible. Keep only the ids of the cards still in the list.
            const currentIds = new Set([...container.querySelectorAll('.meter-card[data-instrument-id]')].map(card => card.dataset.instrumentId));
            for (const id of [...visibleIds]) {
                if (!currentIds.has(id)) {
                    visibleIds.delete(id);
                }
            }
            scheduleReport();
        }
    });
    mutationObserver.observe(container, { childList: true });

    return {
        dispose() {
            disposed = true;
            clearTimeout(reportTimer);
            intersectionObserver.disconnect();
            mutationObserver.disconnect();
        }
    };
}
