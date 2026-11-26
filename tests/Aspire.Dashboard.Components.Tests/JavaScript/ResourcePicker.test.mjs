// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from "node:assert/strict";
import { test } from "node:test";
import { setResourcePickerOpen } from "../../../src/Aspire.Dashboard/Components/Layout/ResourcesLayout.razor.js";

function createPicker(row) {
    let open = false;
    const scrolls = [];
    const picker = Object.assign(new EventTarget(), {
        matches: selector => selector === ":popover-open" && open,
        togglePopover(value) { open = value; },
        querySelector: selector => selector === ".resource-pane-list" ? list : null,
    });
    const list = {
        clientTop: 0,
        clientHeight: 100,
        getBoundingClientRect: () => ({ top: 100 }),
        querySelector: selector => selector === ".resource-row.selected" && row ? {
            getBoundingClientRect() {
                assert.equal(open, true, "Measure the selection after opening the popover");
                return row;
            },
        } : null,
        scrollBy: options => scrolls.push(options),
    };
    const layout = { querySelector: selector => selector === ":scope > .resource-picker-popover" ? picker : null };
    return { setOpen: value => setResourcePickerOpen(layout, {}, value), scrolls };
}

test("opening the picker reveals a selection below the viewport only on open", () => {
    const { setOpen, scrolls } = createPicker({ top: 190, bottom: 220 });
    setOpen(true);
    setOpen(true);
    setOpen(false);
    assert.deepEqual(scrolls, [{ top: 20, behavior: "instant" }]);

    setOpen(true);
    assert.deepEqual(scrolls, [{ top: 20, behavior: "instant" }, { top: 20, behavior: "instant" }]);
});

test("opening the picker reveals a selection above the viewport", () => {
    const { setOpen, scrolls } = createPicker({ top: 75, bottom: 105 });
    setOpen(true);
    assert.deepEqual(scrolls, [{ top: -25, behavior: "instant" }]);
});

test("opening the picker keeps the scroll position for a visible or missing selection", () => {
    for (const row of [{ top: 105, bottom: 135 }, null]) {
        const { setOpen, scrolls } = createPicker(row);
        setOpen(true);
        assert.deepEqual(scrolls, []);
    }
});
