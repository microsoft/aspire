// Deck menu positioning, dismissal, and arrow-key focus. The menu is a fixed-layer surface placed
// under its anchor (button menus) or at the cursor (context menus). Colocated module: no inline
// script, so it's CSP-safe.

// Keyed by menu id so multiple menus can coexist and be disposed independently.
const registrations = new Map();

export function initialize(menuElement, menuId, mode, anchorId, cursorX, cursorY, dotNetHelper) {
    const reposition = () => position(menuElement, mode, anchorId, cursorX, cursorY);
    reposition();

    const anchor = anchorId ? document.getElementById(anchorId) : null;

    const onPointerDown = (e) => {
        if (menuElement.contains(e.target)) {
            return;
        }
        // Clicking the anchor is handled by the anchor's own toggle; don't double-close here.
        if (anchor && anchor.contains(e.target)) {
            return;
        }
        dotNetHelper.invokeMethodAsync("CloseFromJs");
    };

    const onKeyDown = (e) => {
        switch (e.key) {
            case "Escape":
                dotNetHelper.invokeMethodAsync("CloseFromJs");
                break;
            case "ArrowDown":
                e.preventDefault();
                moveFocus(menuElement, 1);
                break;
            case "ArrowUp":
                e.preventDefault();
                moveFocus(menuElement, -1);
                break;
        }
    };

    // Defer attaching the outside-click handler so the opening click doesn't immediately close it.
    const attachTimer = setTimeout(() => {
        document.addEventListener("pointerdown", onPointerDown, true);
    }, 0);

    menuElement.addEventListener("keydown", onKeyDown);
    window.addEventListener("resize", reposition, true);
    window.addEventListener("scroll", reposition, true);

    // Move focus into the menu so keyboard navigation works immediately.
    const firstItem = menuElement.querySelector('[role="menuitem"]:not([disabled])');
    if (firstItem) {
        firstItem.focus();
    }

    registrations.set(menuId, () => {
        clearTimeout(attachTimer);
        document.removeEventListener("pointerdown", onPointerDown, true);
        menuElement.removeEventListener("keydown", onKeyDown);
        window.removeEventListener("resize", reposition, true);
        window.removeEventListener("scroll", reposition, true);
    });
}

export function dispose(menuId) {
    const cleanup = registrations.get(menuId);
    if (cleanup) {
        cleanup();
        registrations.delete(menuId);
    }
}

function moveFocus(menuElement, delta) {
    // Navigate the direct enabled items of the menu that actually contains the focused item, so an
    // open submenu's arrow keys move through that submenu's own siblings rather than jumping back to
    // the top-level menu. The containing menu is the nearest role="menu" ancestor of the focused
    // element (submenus render as nested role="menu" surfaces); fall back to the root menu element
    // when nothing is focused yet.
    const active = document.activeElement;
    const containingMenu = (active && active.closest('[role="menu"]')) || menuElement;

    // Items can appear directly (leaf buttons) or wrapped for submenu triggers
    // (.deck-menu__item-wrapper > button[role="menuitem"]). Support both shapes at one level deep so
    // only the current menu's siblings participate, not items nested inside its child submenus.
    const items = Array.from(containingMenu.querySelectorAll(':scope > [role="menuitem"]:not([disabled]), :scope > .deck-menu__item-wrapper > [role="menuitem"]:not([disabled])'));
    if (items.length === 0) {
        return;
    }

    const currentIndex = items.indexOf(active);
    let nextIndex = currentIndex + delta;
    if (nextIndex < 0) {
        nextIndex = items.length - 1;
    } else if (nextIndex >= items.length) {
        nextIndex = 0;
    }

    items[nextIndex].focus();
}

function position(menuElement, mode, anchorId, cursorX, cursorY) {
    const menuRect = menuElement.getBoundingClientRect();
    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;
    const margin = 4;

    let top;
    let left;

    if (mode === "anchor" && anchorId) {
        const anchor = document.getElementById(anchorId);
        if (anchor) {
            const anchorRect = anchor.getBoundingClientRect();
            const spaceBelow = viewportHeight - anchorRect.bottom;
            const openAbove = spaceBelow < menuRect.height + margin && anchorRect.top > spaceBelow;

            top = openAbove ? anchorRect.top - menuRect.height - margin : anchorRect.bottom + margin;
            left = anchorRect.left;
        } else {
            top = margin;
            left = margin;
        }
    } else {
        // Cursor / context menu: open below-right of the cursor, flipping when there isn't room.
        top = cursorY + menuRect.height + margin > viewportHeight ? cursorY - menuRect.height : cursorY;
        left = cursorX + menuRect.width + margin > viewportWidth ? cursorX - menuRect.width : cursorX;
    }

    left = Math.max(margin, Math.min(left, viewportWidth - menuRect.width - margin));
    top = Math.max(margin, Math.min(top, viewportHeight - menuRect.height - margin));

    menuElement.style.top = `${Math.round(top)}px`;
    menuElement.style.left = `${Math.round(left)}px`;
}
