// Deck combobox keyboard glue (Components/Deck/Combobox.razor).
//
// When the combobox is used inside an EditForm (e.g. FilterDialog), pressing Enter while a popup
// option is active must select that option WITHOUT also triggering the browser's implicit form
// submission (which would submit/close the dialog). Blazor's own @onkeydown handler performs the
// selection; here we only cancel the native default so selecting an option doesn't double as a
// submit. When no option is active, Enter is left alone so the form submits normally.
//
// The decision is read from the input's data-active-option attribute, which Blazor keeps in sync
// with the popup's active state each render, so this stays a precise, Enter-only, active-only
// preventDefault (typing and Tab are never blocked). Colocated module: no inline script, CSP-safe.

export function initialize(input) {
    if (!input) {
        return;
    }

    // Guard against double-initialization across re-renders.
    disposeCore(input);

    const onKeyDown = (e) => {
        if (e.key === "Enter" && input.dataset.activeOption === "true") {
            e.preventDefault();
        }
    };

    input.addEventListener("keydown", onKeyDown);
    input.deckComboboxKeyDown = onKeyDown;
}

export function dispose(input) {
    disposeCore(input);
}

function disposeCore(input) {
    const onKeyDown = input?.deckComboboxKeyDown;
    if (!onKeyDown) {
        return;
    }

    input.removeEventListener("keydown", onKeyDown);
    delete input.deckComboboxKeyDown;
}
