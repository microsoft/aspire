// Escape-to-close for the Deck drawer (Components/Deck/Drawer.razor).
//
// We attach the keydown listener in JS rather than using Blazor's @onkeydown because the
// dashboard's pinned blazor.web.js serializes the keyboard event's `isComposing` field, which the
// server-side KeyboardEventArgsReader rejects with "Unknown property isComposing" — throwing a
// framework error on every Escape press. Forwarding only an Escape signal (no KeyboardEventArgs)
// sidesteps that deserialization path entirely, mirroring how the dashboard's global shortcuts in
// app.js pass a primitive to .NET instead of a KeyboardEventArgs.
export function registerDrawerEscape(element, dotNetRef) {
    if (!element) {
        return;
    }

    disposeDrawerEscape(element);

    const onKeyDown = event => {
        // Ignore Escape while an IME composition is active (the same `isComposing` field that breaks
        // the framework reader) so cancelling composition doesn't also close the drawer.
        if (event.key === "Escape" && !event.isComposing) {
            dotNetRef.invokeMethodAsync("CloseFromJavaScript");
        }
    };

    element.addEventListener("keydown", onKeyDown);
    element.deckDrawerKeyDown = onKeyDown;
}

export function disposeDrawerEscape(element) {
    const onKeyDown = element?.deckDrawerKeyDown;
    if (!onKeyDown) {
        return;
    }

    element.removeEventListener("keydown", onKeyDown);
    delete element.deckDrawerKeyDown;
}
