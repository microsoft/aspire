const registrations = new WeakMap();

export function initializeMobileNavMenu(menu, navigationButtonId, dotNetReference) {
    const onFocusOut = () => {
        setTimeout(() => {
            if (!menu.hidden && !menu.contains(document.activeElement)) {
                dotNetReference.invokeMethodAsync("CloseFromNavigation");
            }
        });
    };

    const onKeyDown = event => {
        if (menu.hidden) {
            return;
        }

        if (event.key === "Escape") {
            event.preventDefault();
            dotNetReference.invokeMethodAsync("CloseFromNavigation").then(() => {
                document.getElementById(navigationButtonId)?.focus();
            });
            return;
        }

        const items = Array.from(menu.querySelectorAll(".mobile-nav-menu-item:not(:disabled)"));
        if (items.length === 0) {
            return;
        }

        const currentIndex = items.indexOf(document.activeElement);
        let nextIndex;
        switch (event.key) {
            case "ArrowDown":
                nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % items.length;
                break;
            case "ArrowUp":
                nextIndex = currentIndex <= 0 ? items.length - 1 : currentIndex - 1;
                break;
            case "Home":
                nextIndex = 0;
                break;
            case "End":
                nextIndex = items.length - 1;
                break;
            default:
                return;
        }

        event.preventDefault();
        items[nextIndex].focus();
        items[nextIndex].scrollIntoView({ block: "nearest" });
    };

    menu.addEventListener("focusout", onFocusOut);
    document.addEventListener("keydown", onKeyDown);
    registrations.set(menu, { onFocusOut, onKeyDown });
}

export function disposeMobileNavMenu(menu) {
    const registration = registrations.get(menu);
    if (!registration) {
        return;
    }

    menu.removeEventListener("focusout", registration.onFocusOut);
    document.removeEventListener("keydown", registration.onKeyDown);
    registrations.delete(menu);
}
