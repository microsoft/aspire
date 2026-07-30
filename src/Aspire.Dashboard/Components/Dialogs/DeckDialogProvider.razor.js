// Deck dialog accessibility glue (Components/Dialogs/DeckDialogProvider.razor).
//
// Colocated module (no inline script, so it's CSP-safe). It provides the modal-dialog semantics
// that the Deck dialog markup can't express on its own:
//   * initial focus moves into the dialog (first focusable control, else the dialog container),
//   * Tab / Shift+Tab focus trapping while TrapFocus is set,
//   * a body scroll lock while any PreventScroll dialog is open (reference-counted so a second
//     stacked dialog doesn't unlock the page until the last one closes), and
//   * focus restoration to the element that had focus before the dialog opened.
//
// Each open dialog registers under its stable element id so stacked dialogs are tracked and
// disposed independently.

const registrations = new Map(); // dialogElementId -> cleanup()

// Reference count so multiple stacked scroll-locking dialogs share one body lock. Only the first
// lock captures the original overflow and only the last release restores it; otherwise closing an
// inner dialog would prematurely re-enable page scrolling behind an outer dialog.
let scrollLockCount = 0;
let savedBodyOverflow = "";

// Elements that can receive focus. `[tabindex="-1"]` is intentionally excluded: those are
// programmatically focusable but not part of the sequential Tab order we trap.
const FOCUSABLE_SELECTOR = [
    "a[href]",
    "button:not([disabled])",
    "input:not([disabled])",
    "select:not([disabled])",
    "textarea:not([disabled])",
    "[tabindex]:not([tabindex=\"-1\"])",
].join(",");

function getFocusable(container) {
    // offsetParent is null for elements that are hidden (display:none) or in a hidden subtree, so
    // this filters out controls in collapsed regions. The activeElement fallback keeps a currently
    // focused element (e.g. one with position:fixed, whose offsetParent is null) in the list.
    return Array.from(container.querySelectorAll(FOCUSABLE_SELECTOR))
        .filter(el => el.offsetParent !== null || el === document.activeElement);
}

export function initialize(dialogElementId, options) {
    const dialog = document.getElementById(dialogElementId);
    if (!dialog) {
        return;
    }

    // Guard against double initialization: a re-render after Hide/Show reuses the same id.
    disposeCore(dialogElementId);

    const opts = options || {};

    // Remember what had focus so we can restore it when the dialog closes.
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;

    // Initial focus: the first focusable control inside the dialog, otherwise the dialog container
    // itself (the provider gives it tabindex="-1" so it can hold focus when it has no controls).
    const initialFocusable = getFocusable(dialog);
    if (initialFocusable.length > 0) {
        initialFocusable[0].focus();
    } else {
        dialog.focus();
    }

    let onKeyDown = null;
    if (opts.trapFocus) {
        onKeyDown = (e) => {
            if (e.key !== "Tab") {
                return;
            }

            const items = getFocusable(dialog);
            if (items.length === 0) {
                // Nothing to Tab to; keep focus on the dialog container.
                e.preventDefault();
                dialog.focus();
                return;
            }

            const first = items[0];
            const last = items[items.length - 1];
            const active = document.activeElement;

            if (e.shiftKey) {
                // Shift+Tab off the first control (or from outside the dialog) wraps to the last.
                if (active === first || !dialog.contains(active)) {
                    e.preventDefault();
                    last.focus();
                }
            } else if (active === last || !dialog.contains(active)) {
                // Tab off the last control (or from outside the dialog) wraps to the first.
                e.preventDefault();
                first.focus();
            }
        };

        dialog.addEventListener("keydown", onKeyDown);
    }

    let lockedScroll = false;
    if (opts.preventScroll) {
        if (scrollLockCount === 0) {
            savedBodyOverflow = document.body.style.overflow;
            document.body.style.overflow = "hidden";
        }
        scrollLockCount++;
        lockedScroll = true;
    }

    registrations.set(dialogElementId, () => {
        if (onKeyDown) {
            dialog.removeEventListener("keydown", onKeyDown);
        }

        if (lockedScroll) {
            scrollLockCount = Math.max(0, scrollLockCount - 1);
            if (scrollLockCount === 0) {
                document.body.style.overflow = savedBodyOverflow;
                savedBodyOverflow = "";
            }
        }

        // Only restore focus if the previous element is still in the document; a dialog opened from
        // a control that has since been removed shouldn't throw or steal focus somewhere invalid.
        if (previouslyFocused && previouslyFocused.isConnected) {
            previouslyFocused.focus();
        }
    });
}

export function dispose(dialogElementId) {
    disposeCore(dialogElementId);
}

function disposeCore(dialogElementId) {
    const cleanup = registrations.get(dialogElementId);
    if (cleanup) {
        cleanup();
        registrations.delete(dialogElementId);
    }
}
