// Fits a line of tags into its width and replaces the tags that don't fit with a "+N" tag:
//
//   <aspire-tag-overflow>
//       <span>frontend (3)</span>
//       <span>catalogservice (2)</span>
//       <span>catalogdb (1)</span>
//       <span data-tag-overflow-more aria-hidden="true"></span>
//   </aspire-tag-overflow>
//
// Every element child except the [data-tag-overflow-more] one is a tag. Tags that don't fit get
// [data-tag-overflow-hidden], which hides them visually but keeps them in the accessibility tree, so a
// screen reader still reads every tag. The "+N" element gets [data-count] (rendered with CSS) and
// [data-tooltip] (shown by the shared tooltip provider) listing the hidden tags. Blazor never renders
// these attributes, so a later render doesn't overwrite them.
//
// FluentOverflow solves the same problem, but each instance un-hides its items, reads its width and
// hides them again in its own animation frame callback, and reports the result to the server, which
// renders the count. Every instance therefore forces a synchronous layout of the whole document, and
// the server round trip causes another one. The traces list renders over 100 of them while telemetry
// streams in, which blocked the main thread for 100+ ms at a time and delayed clicks and navigation.
// Here all instances share one animation frame callback that first puts back every pending tag, then
// measures every pending instance, then hides what doesn't fit, so a frame forces at most one layout.

const tagOverflowTagName = "aspire-tag-overflow";
const moreSelector = ":scope > [data-tag-overflow-more]";
const hiddenAttribute = "data-tag-overflow-hidden";

const connected = new Set();
const pending = new Set();
const observedWidths = new WeakMap();
let frame = 0;

// One observer for all instances. A tag line is sized by its container (a grid cell), not by its
// tags, so its width only changes when a column, the window or a panel is resized.
const resizeObserver = new ResizeObserver(entries => {
    for (const entry of entries) {
        const width = entry.contentRect.width;
        if (observedWidths.get(entry.target) !== width) {
            observedWidths.set(entry.target, width);
            schedule(entry.target);
        }
    }
});

// Tag widths change when a web font finishes loading, without changing the line's own width.
document.fonts?.addEventListener("loadingdone", () => connected.forEach(schedule));

function schedule(element) {
    pending.add(element);
    frame ||= requestAnimationFrame(update);
}

function update() {
    frame = 0;
    const elements = [...pending].filter(element => element.isConnected);
    pending.clear();

    // Write: put every tag back in the line and size the "+N" tag for the largest possible count.
    const lines = elements.map(element => {
        const more = element.querySelector(moreSelector);
        const tags = [...element.children].filter(child => child !== more);
        for (const tag of tags) {
            tag.removeAttribute(hiddenAttribute);
        }
        if (more) {
            more.setAttribute("data-count", `+${tags.length}`);
        }
        return { element, more, tags };
    });

    // Read: the first read lays out the document once, and the reads after it reuse that layout.
    for (const line of lines) {
        const style = getComputedStyle(line.element);
        line.available = line.element.getBoundingClientRect().width;
        line.gap = Number.parseFloat(style.columnGap) || 0;
        line.widths = line.tags.map(outerWidth);
        line.moreWidth = line.more ? outerWidth(line.more) : 0;
    }

    // Write: hide the tags that don't fit.
    for (const line of lines) {
        const visibleCount = countVisibleTags(line);
        const hidden = line.tags.slice(visibleCount);
        for (const tag of hidden) {
            tag.setAttribute(hiddenAttribute, "");
        }
        if (!line.more) {
            continue;
        }
        if (hidden.length === 0) {
            line.more.removeAttribute("data-count");
            line.more.removeAttribute("data-tooltip");
        } else {
            line.more.setAttribute("data-count", `+${hidden.length}`);
            line.more.setAttribute("data-tooltip", hidden.map(getTagText).join("\n"));
        }
    }
}

// Returns how many leading tags fit. When they don't all fit, the "+N" tag must fit after them too.
function countVisibleTags({ tags, widths, gap, available, moreWidth }) {
    const total = widths.reduce((sum, width) => sum + width, 0) + gap * Math.max(0, tags.length - 1);
    if (total <= available) {
        return tags.length;
    }

    let used = moreWidth;
    let count = 0;
    while (count < tags.length && used + widths[count] + gap <= available) {
        used += widths[count] + gap;
        count++;
    }
    return count;
}

function outerWidth(element) {
    const style = getComputedStyle(element);
    return element.getBoundingClientRect().width +
        (Number.parseFloat(style.marginInlineStart) || 0) +
        (Number.parseFloat(style.marginInlineEnd) || 0);
}

// Tag markup spreads text over several nodes, for example "frontend", " (", "3", ")".
function getTagText(tag) {
    return tag.textContent.replace(/\s+/g, " ").trim();
}

class AspireTagOverflow extends HTMLElement {
    #mutationObserver = null;

    connectedCallback() {
        connected.add(this);
        // Blazor can insert this element before its children, and it updates tag text in place when
        // a trace receives more spans, so content changes trigger a new measurement. Attributes
        // other than class and style are ignored because this script writes them.
        this.#mutationObserver = new MutationObserver(() => schedule(this));
        this.#mutationObserver.observe(this, {
            childList: true,
            characterData: true,
            subtree: true,
            attributes: true,
            attributeFilter: ["class", "style"]
        });
        resizeObserver.observe(this);
        schedule(this);
    }

    disconnectedCallback() {
        connected.delete(this);
        pending.delete(this);
        this.#mutationObserver?.disconnect();
        this.#mutationObserver = null;
        resizeObserver.unobserve(this);
        observedWidths.delete(this);
    }
}

customElements.define(tagOverflowTagName, AspireTagOverflow);
