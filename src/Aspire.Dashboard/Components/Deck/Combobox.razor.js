// Deck combobox keyboard glue (Components/Deck/Combobox.razor).
//
// When the combobox is used inside an EditForm (e.g. FilterDialog), pressing Enter while a popup
// option is active must select that option WITHOUT also triggering the browser's implicit form
// submission (which would submit/close the dialog). Blazor's own @onkeydown handler performs the
// selection; here we only cancel the native default so selecting an option doesn't double as a
// submit. When no option is active, Enter is left alone so the form submits normally.
//
// Interactive Server can receive ArrowDown and Enter before the ArrowDown render returns. Track the
// arrow activation entirely in the browser so Enter is cancelled before the enclosing form can
// submit, without consulting server-rendered state that may be stale. Colocated module: no inline
// script, CSP-safe.

export function initialize(input) {
    if (!input) {
        return;
    }

    // Guard against double-initialization across re-renders.
    disposeCore(input);

    let keyboardOptionActive = false;

    const resetKeyboardOption = () => {
        keyboardOptionActive = false;
    };

    const onKeyDown = (e) => {
        // Arrow navigation itself establishes the synchronous interaction state. Do not consult
        // server-rendered option data here: it can lag behind rapid input/filtering events.
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            keyboardOptionActive = true;
            return;
        }

        if (e.key === "Enter" && keyboardOptionActive) {
            e.preventDefault();
            keyboardOptionActive = false;
            return;
        }

        if (e.key === "Escape") {
            keyboardOptionActive = false;
        }
    };

    const root = input.closest(".deck-combobox");
    const onOptionMouseDown = (e) => {
        if (e.target instanceof Element && e.target.closest(".deck-combobox__option")) {
            resetKeyboardOption();
        }
    };

    input.addEventListener("keydown", onKeyDown);
    input.addEventListener("input", resetKeyboardOption);
    input.addEventListener("blur", resetKeyboardOption);
    root?.addEventListener("mousedown", onOptionMouseDown);
    input.deckComboboxKeyDown = onKeyDown;
    input.deckComboboxResetKeyboardOption = resetKeyboardOption;
    input.deckComboboxRoot = root;
    input.deckComboboxOptionMouseDown = onOptionMouseDown;
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
    input.removeEventListener("input", input.deckComboboxResetKeyboardOption);
    input.removeEventListener("blur", input.deckComboboxResetKeyboardOption);
    input.deckComboboxRoot?.removeEventListener("mousedown", input.deckComboboxOptionMouseDown);
    delete input.deckComboboxKeyDown;
    delete input.deckComboboxResetKeyboardOption;
    delete input.deckComboboxRoot;
    delete input.deckComboboxOptionMouseDown;
}
