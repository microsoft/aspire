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

const openWindows = new Map();
const launchers = new Map();
let pollHandle = null;

// The opener finds out about a closed popup by polling `closed` rather than by listening for a `pagehide` message
// from the popup. `pagehide` does not fire when the tab crashes or is force-closed by the OS, and a terminal that is
// wedged in a "running in a separate window" state with no way back is much worse than a poll that ticks twice a
// second while a window happens to be open.
const POLL_INTERVAL_MS = 400;

const DEFAULT_FEATURES = 'popup=yes,resizable=yes,scrollbars=no,menubar=no,toolbar=no,location=no,status=no';

export function registerTerminalWindowButton(buttonId, id, owner) {
    unregisterTerminalWindowButton(id);
    const button = document.getElementById(buttonId);
    if (!button) {
        throw new Error('The terminal window button is no longer available.');
    }
    const launcher = { button, owner, pending: Promise.resolve(), disposed: false };
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

        const entry = openWindows.get(key);
        notify(launcher, () => {
            // A queued result must not resurrect a window that closed or was returned to the dock meanwhile.
            if (result === 'opened' || result === 'focused') {
                if (openWindows.get(key) !== entry || entry.win.closed) {
                    return;
                }
            }
            return owner.invokeMethodAsync('OnTerminalWindowOpenedAsync', key, result);
        });
    };
    launchers.set(id, launcher);
    button.addEventListener('click', launcher.click);
}

export function unregisterTerminalWindowButton(id) {
    const launcher = launchers.get(id);
    if (!launcher) {
        return;
    }
    launcher.disposed = true;
    launcher.button.removeEventListener('click', launcher.click);
    launchers.delete(id);
    for (const [key, entry] of openWindows) {
        if (entry.owner === launcher) {
            openWindows.delete(key);
        }
    }
    stopPollingIfEmpty();
}

function notify(launcher, callback) {
    // Serialize notifications, NOT browser operations. A slow open acknowledgement cannot delay a subsequent
    // click's popup, and a close notification cannot overtake the corresponding detach acknowledgement.
    launcher.pending = launcher.pending.then(() => {
        if (!launcher.disposed) {
            return callback();
        }
    }).catch(error => console.warn('Could not update terminal window state in the dashboard.', error));
}

/**
 * Opens a terminal in its own window, or focuses the window if one is already open for this key.
 * @returns {'opened'|'focused'|'blocked'}
 */
function openTerminalWindow(key, url, width, height, owner) {
    const existing = openWindows.get(key);
    if (existing && !existing.win.closed) {
        existing.win.focus();
        existing.owner = owner;
        return 'focused';
    }

    const features = `${DEFAULT_FEATURES},width=${Math.round(width)},height=${Math.round(height)}`;

    // A name makes the popup reusable: if the user closed the tab that opened it and detaches again, the browser
    // targets the same window instead of stacking a second one on top of it.
    const win = window.open(url, windowNameFor(key), features);
    if (!win) {
        // Blocked. The caller surfaces this, because a silently missing window looks like the terminal was lost.
        return 'blocked';
    }

    openWindows.set(key, { win, owner });
    ensurePolling();
    return 'opened';
}

export function focusTerminalWindow(key) {
    const entry = openWindows.get(key);
    if (!entry || entry.win.closed) {
        return false;
    }

    entry.win.focus();
    return true;
}

/**
 * Closes the window for this key. No close notification is raised: the caller is the one asking, so it already
 * knows to reattach, and dropping the entry here keeps the poll from reporting a close the caller initiated.
 */
export function closeTerminalWindow(key) {
    const entry = openWindows.get(key);
    openWindows.delete(key);

    if (entry && !entry.win.closed) {
        entry.win.close();
    }
    stopPollingIfEmpty();
}

export function isTerminalWindowOpen(key) {
    const entry = openWindows.get(key);
    return !!entry && !entry.win.closed;
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
            if (!entry.win.closed) {
                continue;
            }

            openWindows.delete(key);

            notify(entry.owner, () => {
                if (!openWindows.has(key)) {
                    return entry.owner.owner.invokeMethodAsync('OnTerminalWindowClosedAsync', key);
                }
            });
        }

        stopPollingIfEmpty();
    }, POLL_INTERVAL_MS);
}

function stopPollingIfEmpty() {
    if (openWindows.size === 0 && pollHandle !== null) {
        clearInterval(pollHandle);
        pollHandle = null;
    }
}
