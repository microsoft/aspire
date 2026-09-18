// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Tracks terminal sessions that the user has popped out into their own browser window.
//
// A detached window is not a move: terminals are multi-headed (HMP1 supports several viewers on one PTY), and the
// popup navigates to the dashboard on its own, so it stays alive even if the opener is reloaded or closed. All this
// module owns is the window handle, so the page that opened it can focus it, close it, and find out when the user
// closed it themselves.
//
// Keys are opaque strings chosen by the caller: a dock terminal id, or "resource:<name>:<replica>". They only have to
// be stable and unique within the page.
// Dock windows also have a durable, generation-scoped record. After a document reload that record keeps the dock
// on its placeholder until the independent page supplies its WindowProxy through a same-origin message.
// A missing response is NOT proof of closure: background pages can be suspended indefinitely.

const openWindows = new Map();
const launchers = new Map();
const detachedPages = new Map();
let openerContext = null;
let pollHandle = null;
const RECORD_PREFIX = 'aspire-terminal-window:';
const REQUEST_PREFIX = 'aspire-terminal-window-request:';
const OWNER_PREFIX = 'aspire-terminal-window-owner:';
const MESSAGE_TYPE = 'aspire-terminal-window-ready';

// The opener finds out about a closed popup by polling `closed` rather than by listening for a `pagehide` message
// from the popup. `pagehide` does not fire when the tab crashes or is force-closed by the OS, and a terminal that is
// wedged in a "running in a separate window" state with no way back is much worse than a poll that ticks twice a
// second while a window happens to be open.
const POLL_INTERVAL_MS = 400;

const DEFAULT_FEATURES = 'popup=yes,resizable=yes,scrollbars=no,menubar=no,toolbar=no,location=no,status=no';

export function registerTerminalWindowButton(buttonId, id, owner, baseUri) {
    unregisterTerminalWindowButton(id);
    const button = document.getElementById(buttonId);
    if (!button) {
        throw new Error('The terminal window button is no longer available.');
    }
    const launcher = { button, owner, baseUri, dockKeys: new Set(), pending: Promise.resolve(), disposed: false };
    launcher.click = () => {
        if (launcher.disposed || !button.isConnected || button.disabled || button.hasAttribute('disabled') ||
            button.getAttribute('aria-disabled') === 'true' || button.closest('[inert], [hidden]')) {
            return;
        }

        // Read the rendered metadata, not registration-time parameters: Blazor may have changed the active tab
        // or font since wiring this listener. Missing metadata is an unconfigured button, not a blank popup.
        const key = button.getAttribute('data-terminal-window-key');
        const url = button.getAttribute('data-terminal-window-url');
        if (!key || !url) {
            return;
        }

        let result;
        try {
            // Transient activation is window-scoped and can survive async work, but expires on a browser-defined
            // timer. Opening here avoids dependence on server latency; popup policy can still block this call.
            // https://html.spec.whatwg.org/multipage/interaction.html#tracking-user-activation
            result = openTerminalWindow(key, url, 960, 600, launcher);
        } catch (error) {
            console.error('Failed to open or focus the terminal window.', error);
            result = 'failed';
        }

        notifyLaunch(launcher, key, result);
    };
    launcher.focusClick = event => {
        const focusButton = event.target.closest?.('[data-terminal-window-focus-key]');
        const group = button.getAttribute('data-terminal-window-focus-group');
        if (!group || !focusButton || focusButton.getAttribute('data-terminal-window-focus-group') !== group ||
            launcher.disposed || !focusButton.isConnected || focusButton.disabled ||
            focusButton.hasAttribute('disabled') || focusButton.closest('[inert], [hidden]')) {
            return;
        }
        const key = focusButton.getAttribute('data-terminal-window-focus-key');
        let result;
        try {
            const entry = openWindows.get(key);
            if (!entry) {
                throw new Error('The detached terminal window is no longer tracked.');
            }
            // Recovery without a handle may need to reuse a named target. Only this native user click can do
            // that; discovery, storage events, and polling must never call window.open.
            result = openTerminalWindow(key, entry.record?.url, 960, 600, launcher);
        } catch (error) {
            console.error('Failed to focus the terminal window.', error);
            result = 'failed';
        }
        notifyLaunch(launcher, key, result);
    };
    launchers.set(id, launcher);
    button.addEventListener('click', launcher.click);
    document.addEventListener('click', launcher.focusClick);
}

function notifyLaunch(launcher, key, result) {
    const entry = openWindows.get(key);
    const record = entry?.record;
    notify(launcher, () => {
        if (['opened', 'focused', 'adopted', 'recovering'].includes(result)) {
            if (openWindows.get(key) !== entry || entry.owner !== launcher || entry.record !== record || entry.win?.closed) {
                return;
            }
        }
        return launcher.owner.invokeMethodAsync('OnTerminalWindowOpenedAsync', key, result);
    });
}

export function unregisterTerminalWindowButton(id) {
    const launcher = launchers.get(id);
    if (!launcher) {
        return;
    }
    launcher.disposed = true;
    launcher.button.removeEventListener('click', launcher.click);
    document.removeEventListener('click', launcher.focusClick);
    launchers.delete(id);
    for (const entry of openWindows.values()) {
        if (entry.owner === launcher) {
            // The component owns the listener, not the independent viewer. Retain its handle for a replacement
            // component, but release the old circuit reference and never notify that disposed owner again.
            entry.owner = null;
        }
    }
    releaseUnusedContext();
}

export function adoptTerminalWindows(id, keys) {
    const launcher = launchers.get(id);
    if (!launcher) {
        throw new Error('The terminal window launcher is no longer available.');
    }

    // Only adopt the caller's current terminal identities, never every window in this module. In particular,
    // AppHost dock IDs come from its metadata snapshot, not resource names or persisted titles from another run.
    const context = getOpenerContext(launcher.baseUri);
    const notifications = [];
    for (const key of keys) {
        launcher.dockKeys.add(key);
        let entry = openWindows.get(key);
        let record = readRecord(context, key);
        if (entry?.win?.closed) {
            removeRecord(entry);
            openWindows.delete(key);
            entry = null;
            record = readRecord(context, key);
        }
        if (record && entry?.record?.generation !== record.generation) {
            entry = { win: null, owner: launcher, record, context };
            openWindows.set(key, entry);
        }
        if (!entry) {
            continue;
        }

        entry.owner = launcher;
        notifications.push(notify(launcher, () => {
            if (openWindows.get(key) === entry && entry.owner === launcher && !entry.win?.closed) {
                // Reuse the detach acknowledgement without focusing or navigating the independent window.
                return launcher.owner.invokeMethodAsync('OnTerminalWindowOpenedAsync', key, entry.win ? 'adopted' : 'recovering');
            }
        }));
    }
    requestDiscovery(context);

    // The dock must reconcile these acknowledgements before mounting ANY candidate viewer. Returning just a
    // snapshot could overtake a close/return notification and resurrect a stale detached state.
    return Promise.all(notifications);
}

function notify(launcher, callback) {
    // Serialize notifications, NOT browser operations. A slow open acknowledgement cannot delay a subsequent
    // click's popup, and a close notification cannot overtake the corresponding detach acknowledgement.
    const notification = launcher.pending.then(() => {
        if (!launcher.disposed) {
            return callback();
        }
    });
    launcher.pending = notification.catch(error => console.warn('Could not update terminal window state in the dashboard.', error));
    // Click/poll notifications are fire-and-forget, but adoption must not enable viewers if reconciliation failed.
    return notification;
}

/**
 * Opens a terminal in its own window, or focuses the window if one is already open for this key.
 * @returns {'opened'|'focused'|'blocked'}
 */
function openTerminalWindow(key, url, width, height, owner) {
    const existing = openWindows.get(key);
    if (existing?.win && !existing.win.closed) {
        existing.win.focus();
        existing.owner = owner;
        return 'focused';
    }

    const features = `${DEFAULT_FEATURES},width=${Math.round(width)},height=${Math.round(height)}`;

    // A name makes the popup reusable: if the user closed the tab that opened it and detaches again, the browser
    // targets the same window instead of stacking a second one on top of it.
    let record;
    let context;
    let createdRecord = false;
    if (owner.dockKeys.has(key)) {
        context = getOpenerContext(owner.baseUri);
        record = readRecord(context, key);
        if (!record || existing?.win?.closed) {
            const target = new URL(url);
            if (target.origin !== new URL(context.baseUri).origin ||
                target.pathname !== terminalPath(context.baseUri, key)) {
                throw new Error('The terminal window URL is outside this dashboard.');
            }
            const generation = newId();
            target.searchParams.set('windowOwner', context.ownerId);
            target.searchParams.set('windowGeneration', generation);
            record = { version: 1, key, generation, url: target.href };
            // Persist BEFORE opening. A main-page reload between window.open and the popup's initialization
            // must not allow the new dock to create a competing auto-fit viewer.
            window.localStorage.setItem(recordKey(context, key), JSON.stringify(record));
            createdRecord = true;
        }
        url = record.url;
    }
    let win;
    try {
        win = window.open(url, record ? coordinatedWindowName(context, record) : windowNameFor(key), features);
    } catch (error) {
        if (createdRecord) {
            removeRecord({ context, record });
        }
        throw error;
    }
    if (!win) {
        if (createdRecord) {
            removeRecord({ context, record });
        }
        // Blocked. The caller surfaces this, because a silently missing window looks like the terminal was lost.
        return 'blocked';
    }

    openWindows.set(key, { win, owner, record, context });
    ensurePolling();
    return 'opened';
}

export function focusTerminalWindow(key) {
    const entry = openWindows.get(key);
    if (!entry || entry.win?.closed) {
        return false;
    }

    // An unresolved durable record is not a closed window. The native focus button can recover its named target.
    entry.win?.focus();
    return true;
}

/**
 * Closes the window for this key. No close notification is raised: the caller is the one asking, so it already
 * knows to reattach, and dropping the entry here keeps the poll from reporting a close the caller initiated.
 */
export function closeTerminalWindow(key) {
    const entry = openWindows.get(key);
    // Revocation is durable before returning control to the dock. A suspended or reloading detached page checks
    // this generation before it mounts again, and a late ready message cannot resurrect a returned window.
    removeRecord(entry);
    if (!entry && openerContext) {
        // Explicit return must also recover from a corrupt record that passive adoption could not parse.
        window.localStorage.removeItem(recordKey(openerContext, key));
    }
    openWindows.delete(key);

    if (entry?.win && !entry.win.closed) {
        entry.win.close();
    }
    stopPollingIfEmpty();
}

export function isTerminalWindowOpen(key) {
    const entry = openWindows.get(key);
    return !!entry && !entry.win?.closed;
}

function windowNameFor(key) {
    // Named targets reuse browsing contexts, so preserve distinctions such as "a.b" versus "a_b".
    // https://developer.mozilla.org/en-US/docs/Web/API/Window/open#target
    return `aspire-terminal-${encodeURIComponent(key)}`;
}

function ensurePolling() {
    if (pollHandle !== null) {
        return;
    }

    pollHandle = setInterval(() => {
        // Snapshot the entries: the .NET callback can re-enter this module (for example by detaching another
        // terminal) and mutate the map while we are walking it.
        for (const [key, entry] of [...openWindows.entries()]) {
            let ended = entry.win?.closed;
            if (!ended && entry.announced) {
                try {
                    ended = entry.win.location.href !== entry.record.url;
                } catch {
                    // A recovered window that navigates to another origin is no longer this terminal viewer.
                    ended = true;
                }
            }
            if (!ended) {
                continue;
            }

            try {
                removeRecord(entry);
                forgetWindow(key, entry);
            } catch (error) {
                reportTrackingFailure(entry, error);
            }
        }

        stopPollingIfEmpty();
    }, POLL_INTERVAL_MS);
}

function stopPollingIfEmpty() {
    if (openWindows.size === 0 && pollHandle !== null) {
        clearInterval(pollHandle);
        pollHandle = null;
    }
    releaseUnusedContext();
}

function newId() {
    // getRandomValues also works on HTTP origins; randomUUID requires a secure context.
    return [...window.crypto.getRandomValues(new Uint32Array(4))].map(value => value.toString(16).padStart(8, '0')).join('');
}

function terminalPath(baseUri, key) {
    return new URL(`terminal-window/apphost/${encodeURIComponent(key)}`, baseUri).pathname;
}

function recordKey(context, key) {
    return RECORD_PREFIX + JSON.stringify([context.baseUri, context.ownerId, key]);
}

function requestKey(context) {
    return REQUEST_PREFIX + JSON.stringify([context.baseUri, context.ownerId]);
}

function coordinatedWindowName(context, record) {
    // A new detach generation gets a distinct browsing context. A delayed return for the old generation must
    // never close a replacement that happens to have the same terminal ID.
    return windowNameFor(JSON.stringify([context.baseUri, context.ownerId, record.key, record.generation]));
}

function readRecord(context, key) {
    const raw = window.localStorage.getItem(recordKey(context, key));
    if (raw === null) {
        return null;
    }
    // Records contain {version:1,key,generation,url}; URL carries fontSize, windowOwner, windowGeneration.
    // Treat corrupt/unavailable storage as a failure, never as evidence that no detached viewer exists.
    const record = JSON.parse(raw);
    const url = new URL(record.url);
    if (record.version !== 1 || record.key !== key || typeof record.generation !== 'string' || !record.generation ||
        url.origin !== new URL(context.baseUri).origin || url.pathname !== terminalPath(context.baseUri, key) ||
        url.searchParams.get('windowOwner') !== context.ownerId || url.searchParams.get('windowGeneration') !== record.generation) {
        throw new Error('Invalid detached terminal window record.');
    }
    return record;
}

function removeRecord(entry) {
    if (entry?.record && readRecord(entry.context, entry.record.key)?.generation === entry.record.generation) {
        window.localStorage.removeItem(recordKey(entry.context, entry.record.key));
    }
}

function getOpenerContext(baseUri) {
    if (openerContext) {
        if (openerContext.baseUri !== baseUri) {
            throw new Error('A terminal launcher cannot change dashboard scope.');
        }
        return openerContext;
    }
    // sessionStorage survives reload but is initially copied into windows opened by this page. Include the
    // browsing-context name to separate those new dashboards, preserving any existing nonempty target name.
    // https://developer.mozilla.org/en-US/docs/Web/API/Window/sessionStorage
    if (!window.name) {
        window.name = `aspire-dashboard-${newId()}`;
    }
    const storageKey = OWNER_PREFIX + baseUri;
    const raw = window.sessionStorage.getItem(storageKey);
    const saved = raw === null ? null : JSON.parse(raw);
    if (saved && (typeof saved.name !== 'string' || typeof saved.id !== 'string' || !saved.id)) {
        throw new Error('Invalid terminal window owner record.');
    }
    const ownerId = saved?.name === window.name ? saved.id : newId();
    window.sessionStorage.setItem(storageKey, JSON.stringify({ name: window.name, id: ownerId }));
    const context = { baseUri, ownerId, documentId: newId() };
    context.message = event => {
        const data = event.data;
        if (event.origin !== new URL(baseUri).origin || data?.type !== MESSAGE_TYPE ||
            data.baseUri !== baseUri || data.ownerId !== ownerId || data.documentId !== context.documentId) {
            return;
        }
        const entry = openWindows.get(data.key);
        if (!entry?.record || entry.record.generation !== data.generation || !event.source ||
            (entry.win && entry.win !== event.source)) {
            return;
        }
        try {
            if (readRecord(context, data.key)?.generation !== data.generation ||
                event.source.name !== coordinatedWindowName(context, entry.record) ||
                event.source.location.href !== entry.record.url) {
                return;
            }
            entry.win = event.source;
            entry.announced = true;
            ensurePolling();
            if (entry.owner) {
                notifyLaunch(entry.owner, data.key, 'adopted');
            }
        } catch (error) {
            reportTrackingFailure(entry, error);
        }
    };
    context.storage = event => {
        for (const [key, entry] of openWindows) {
            if (!entry.record || (event.key !== null && event.key !== recordKey(context, key))) {
                continue;
            }
            try {
                const record = readRecord(context, key);
                if (!record) {
                    forgetWindow(key, entry);
                } else if (record.generation !== entry.record.generation) {
                    entry.win = null;
                    entry.announced = false;
                    entry.record = record;
                    if (entry.owner) {
                        notifyLaunch(entry.owner, key, 'recovering');
                    }
                    requestDiscovery(context);
                }
            } catch (error) {
                reportTrackingFailure(entry, error);
            }
        }
        stopPollingIfEmpty();
    };
    window.addEventListener('message', context.message);
    window.addEventListener('storage', context.storage);
    openerContext = context;
    return context;
}

function requestDiscovery(context) {
    window.localStorage.setItem(requestKey(context), JSON.stringify({ documentId: context.documentId, requestId: newId() }));
}

function forgetWindow(key, entry) {
    if (openWindows.get(key) !== entry) {
        return;
    }
    openWindows.delete(key);
    const owner = entry.owner;
    if (owner) {
        notify(owner, () => {
            if (!openWindows.has(key)) {
                return owner.owner.invokeMethodAsync('OnTerminalWindowClosedAsync', key);
            }
        });
    }
}

function reportTrackingFailure(entry, error) {
    if (entry.trackingFailureReported) {
        return;
    }
    entry.trackingFailureReported = true;
    console.warn('Could not reconcile the detached terminal window.', error);
    if (entry.owner) {
        notifyLaunch(entry.owner, entry.record.key, 'failed');
    }
}

function releaseUnusedContext() {
    if (openerContext && ![...launchers.values()].some(launcher => launcher.dockKeys.size) &&
        ![...openWindows.values()].some(entry => entry.record)) {
        window.removeEventListener('message', openerContext.message);
        window.removeEventListener('storage', openerContext.storage);
        openerContext = null;
    }
}

export function registerDetachedTerminalWindow(id, key, baseUri, owner) {
    unregisterDetachedTerminalWindow(id);
    const url = new URL(window.location.href);
    const ownerId = url.searchParams.get('windowOwner');
    const generation = url.searchParams.get('windowGeneration');
    if (!url.searchParams.has('windowOwner') && !url.searchParams.has('windowGeneration')) {
        return true;
    }
    if (!ownerId || !generation) {
        throw new Error('Incomplete detached terminal window identity.');
    }
    const context = { baseUri, ownerId };
    const record = readRecord(context, key);
    if (!record || record.generation !== generation) {
        window.close();
        return false;
    }
    if (window.name !== coordinatedWindowName(context, record) || url.href !== record.url) {
        throw new Error('The detached terminal window identity does not match its browsing context.');
    }
    const page = { id, context, record, owner, disposed: false };
    const reconcile = () => {
        try {
            if (readRecord(context, key)?.generation !== generation) {
                unregisterDetachedTerminalWindow(id);
                owner.invokeMethodAsync('OnDetachedTerminalWindowRevokedAsync', id)
                    .catch(error => console.warn('Could not release the returned terminal viewer.', error));
                window.close();
                return false;
            }
            const raw = window.localStorage.getItem(requestKey(context));
            if (raw !== null && window.opener && !window.opener.closed) {
                const request = JSON.parse(raw);
                if (typeof request.documentId !== 'string' || !request.documentId) {
                    throw new Error('Invalid terminal window discovery request.');
                }
                // event.source gives the reloaded opener a real WindowProxy, without a passive window.open.
                // https://developer.mozilla.org/en-US/docs/Web/API/Window/postMessage
                window.opener.postMessage({
                    type: MESSAGE_TYPE, baseUri, ownerId, key, generation, documentId: request.documentId,
                }, new URL(baseUri).origin);
            }
            return true;
        } catch (error) {
            unregisterDetachedTerminalWindow(id);
            console.warn('Could not coordinate the detached terminal window.', error);
            owner.invokeMethodAsync('OnDetachedTerminalWindowTrackingFailedAsync', id)
                .catch(error => console.warn('Could not report terminal window coordination failure.', error));
            return false;
        }
    };
    page.storage = event => {
        if (!page.disposed && (event.key === null || event.key === requestKey(context) || event.key === recordKey(context, key))) {
            reconcile();
        }
    };
    detachedPages.set(id, page);
    window.addEventListener('storage', page.storage);
    return reconcile();
}

export function releaseDetachedTerminalWindow(id) {
    const page = detachedPages.get(id);
    if (page) {
        removeRecord(page);
        unregisterDetachedTerminalWindow(id);
    }
}

export function unregisterDetachedTerminalWindow(id) {
    const page = detachedPages.get(id);
    if (page) {
        page.disposed = true;
        window.removeEventListener('storage', page.storage);
        detachedPages.delete(id);
    }
}
