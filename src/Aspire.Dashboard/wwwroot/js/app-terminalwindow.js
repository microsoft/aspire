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
let pollHandle = null;

// The opener finds out about a closed popup by polling `closed` rather than by listening for a `pagehide` message
// from the popup. `pagehide` does not fire when the tab crashes or is force-closed by the OS, and a terminal that is
// wedged in a "running in a separate window" state with no way back is much worse than a poll that ticks twice a
// second while a window happens to be open.
const POLL_INTERVAL_MS = 400;

const DEFAULT_FEATURES = 'popup=yes,resizable=yes,scrollbars=no,menubar=no,toolbar=no,location=no,status=no';

/**
 * Opens a terminal in its own window, or focuses the window if one is already open for this key.
 * @returns {'opened'|'focused'|'blocked'}
 */
export function openTerminalWindow(key, url, width, height, owner) {
    const existing = openWindows.get(key);
    if (existing && !existing.win.closed) {
        existing.win.focus();
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
}

/**
 * Stops tracking a window without closing it. Used when the opening component goes away: the popup is an
 * independent viewer of an AppHost-owned terminal and has no reason to die with the page that spawned it.
 */
export function untrackTerminalWindow(key) {
    openWindows.delete(key);
}

export function isTerminalWindowOpen(key) {
    const entry = openWindows.get(key);
    return !!entry && !entry.win.closed;
}

function windowNameFor(key) {
    return `aspire-terminal-${key.replace(/[^a-zA-Z0-9_-]/g, '_')}`;
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

            // A disposed component leaves a stale reference behind; a failed notification is not worth surfacing
            // because the only consequence is that a page which is already going away misses a UI update.
            entry.owner.invokeMethodAsync('OnTerminalWindowClosedAsync', key).catch(() => { });
        }

        if (openWindows.size === 0) {
            clearInterval(pollHandle);
            pollHandle = null;
        }
    }, POLL_INTERVAL_MS);
}
