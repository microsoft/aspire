const fluentMenuInitializations = new Map();

function completeFluentMenuInitialization(anchorId) {
    const initialization = fluentMenuInitializations.get(anchorId);

    if (!initialization) {
        return;
    }

    if (initialization.timeoutId !== null) {
        clearTimeout(initialization.timeoutId);
    }
    initialization.observer.disconnect();
    fluentMenuInitializations.delete(anchorId);
    initialization.resolve();
}

export function prepareForFluentMenuInitialization(anchorId) {
    completeFluentMenuInitialization(anchorId);

    const anchor = document.getElementById(anchorId);

    if (!anchor) {
        return;
    }

    // Start observing before AspireMenu renders. FluentMenu writes aria-expanded only after its
    // JavaScript modules are initialized, so its first write is an unambiguous readiness signal.
    anchor.removeAttribute("aria-expanded");
    let resolveInitialization;
    const promise = new Promise(resolve => {
        resolveInitialization = resolve;
    });
    const observer = new MutationObserver(() => completeFluentMenuInitialization(anchorId));

    fluentMenuInitializations.set(anchorId, { promise, observer, resolve: resolveInitialization, timeoutId: null });
    observer.observe(anchor, { attributes: true, attributeFilter: ["aria-expanded"] });
}

export function waitForFluentMenuInitialization(anchorId, timeoutMilliseconds) {
    const initialization = fluentMenuInitializations.get(anchorId);

    if (!initialization) {
        return Promise.resolve();
    }

    // FluentMenu normally signals readiness by writing aria-expanded. Bound the wait so a failed
    // module import or a replaced anchor can't leave the Blazor interop call pending indefinitely.
    initialization.timeoutId ??= setTimeout(
        () => completeFluentMenuInitialization(anchorId),
        timeoutMilliseconds);

    return initialization.promise;
}

export function cancelFluentMenuInitialization(anchorId) {
    completeFluentMenuInitialization(anchorId);
}
