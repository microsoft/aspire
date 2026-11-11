// Share one on-demand Fluent tooltip across anchors. Creating a Blazor tooltip per
// log entry or grid cell would add thousands of components to high-throughput telemetry views.
class AspireTooltipProvider extends HTMLElement {
    #controller = null;
    #tooltip = null;
    #content = null;
    #anchor = null;
    #positioningAnchor = null;
    #timer = null;
    #version = 0;
    #observer = null;
    #visibilityObserver = null;
    #keyboardModality = false;
    #focusDismissed = false;

    connectedCallback() {
        this.#controller = new AbortController();
        const options = { signal: this.#controller.signal };
        document.addEventListener("mouseover", event => this.#show(event.target, 250), options);
        // Focus shows a tooltip only while the user navigates with the keyboard. Opening a menu, a dialog or a panel
        // with the mouse moves focus to its first item, and that item's tooltip would otherwise appear although the
        // pointer isn't on it. Track the last input in the capture phase, before focus moves.
        document.addEventListener("focusin", event => {
            if (this.#keyboardModality) {
                this.#focusDismissed = false;
                this.#show(event.target, 0);
            }
        }, options);
        document.addEventListener("mouseout", event => this.#leave(event.relatedTarget), options);
        document.addEventListener("focusout", event => this.#leave(event.relatedTarget), options);
        document.addEventListener("pointerdown", () => {
            this.#keyboardModality = false;
            this.#clear();
        }, { ...options, capture: true });
        document.addEventListener("keydown", event => {
            // Modifier keys alone (for example a shortcut that's being typed) don't mean keyboard navigation.
            if (!["Shift", "Control", "Alt", "Meta"].includes(event.key)) {
                this.#keyboardModality = true;
            }
            if (event.key === "Escape") {
                this.#focusDismissed = true;
                this.#clear();
            }
        }, { ...options, capture: true });
        document.addEventListener("scroll", () => {
            const focused = this.#keyboardModality && !this.#focusDismissed ? document.activeElement : null;
            this.#clear();
            // Tabbing can scroll an overflow container after focusin. Recreate the focused tooltip
            // at its new position even if scrolling also moved the hovered element under the pointer.
            // Pointer tooltips and Escape-dismissed focus tooltips must stay dismissed.
            if (focused) {
                this.#show(focused, 0);
            }
        }, { ...options, capture: true });
        window.addEventListener("resize", () => {
            // SVG positioning anchors snapshot viewport geometry, which is stale after a resize.
            if (this.#positioningAnchor) {
                this.#clear();
            }
        }, options);
    }

    disconnectedCallback() {
        this.#controller.abort();
        this.#clear();
    }

    #getTarget(element) {
        if (!(element instanceof Element)) {
            return null;
        }

        const anchor = element.closest("[data-tooltip], [data-tooltip-content]");
        if (anchor) {
            const contentId = anchor.getAttribute("data-tooltip-content");
            return { anchor, text: anchor.getAttribute("data-tooltip"), content: contentId ? document.getElementById(contentId) : null };
        }

        const option = element.closest("fluent-option");
        const optionMetadata = option?.querySelector("[data-tooltip-option]");
        if (optionMetadata) {
            return { anchor: option, text: optionMetadata.getAttribute("data-tooltip-option") };
        }

        // FluentDataGrid owns the cell element. Keep tooltip metadata inside its content without
        // wrapping that content or changing the grid's flex/grid layout.
        const cell = element.closest("[role=gridcell]");
        const metadata = cell?.querySelector("[data-tooltip-cell]");
        return metadata ? { anchor: cell, text: metadata.getAttribute("data-tooltip-cell") } : null;
    }

    #show(element, delay) {
        if (this.#tooltip?.contains(element)) {
            return;
        }

        const target = this.#getTarget(element);
        if (!target?.text && !target?.content) {
            this.#clear();
            return;
        }

        if (this.#anchor === target.anchor && this.#content?.text === target.text && this.#content?.content === target.content) {
            return;
        }

        this.#clear();
        this.#anchor = target.anchor;
        const version = this.#version;
        this.#timer = setTimeout(async () => {
            this.#timer = null;
            await customElements.whenDefined("fluent-tooltip");
            if (this.#version !== version || !target.anchor.isConnected || !this.isConnected) {
                return;
            }

            const current = this.#getTarget(target.anchor);
            if (!current?.text && !current?.content) {
                this.#clear();
                return;
            }

            target.anchor.id ||= `tooltip-anchor-${crypto.randomUUID()}`;
            const tooltip = document.createElement("fluent-tooltip");
            tooltip.setAttribute("anchor", target.anchor.id);
            const bounds = target.anchor.getBoundingClientRect();
            let positioning = target.anchor.getAttribute("data-tooltip-positioning");
            if (!positioning && target.anchor.matches("fluent-menu-item")) {
                // Keep descriptions beside menus when there is room for Fluent's default 240px
                // maximum width. Full-width mobile menus need above-anchor placement instead.
                if (Math.max(bounds.left, window.innerWidth - bounds.right) >= 240) {
                    positioning = "before";
                }
            }
            if (!positioning && target.anchor.closest(".layout > header")) {
                // Header controls have no room above, so Fluent would flip an "above" tooltip directly
                // into the pointer's downward path. Place it toward the nearest outer edge instead.
                positioning = bounds.left + bounds.width / 2 < window.innerWidth / 2 ? "after" : "before";
            }
            if (!positioning && target.anchor.closest(".resource-pane")) {
                // Keep repeated resource-row actions out of the vertically scrolling pointer path.
                positioning = window.innerWidth - bounds.right >= 240 ? "after" : "before";
            }
            positioning ??= bounds.top < 48 ? "below" : "above";
            tooltip.setAttribute("positioning", positioning);
            this.#setContent(tooltip, current);
            this.#tooltip = tooltip;
            this.appendChild(tooltip);
            if (target.anchor instanceof SVGElement) {
                // SVG graphics don't generate the principal CSS boxes required by anchor positioning.
                // Keep Fluent attached to the real element for accessibility, but position against an HTML box.
                // https://drafts.csswg.org/css-anchor-position-1/#name
                const anchor = document.createElement("span");
                anchor.className = "dashboard-tooltip-anchor";
                anchor.setAttribute("aria-hidden", "true");
                anchor.style.cssText = `position: fixed; pointer-events: none; left: ${bounds.left}px; top: ${bounds.top}px; width: ${bounds.width}px; height: ${bounds.height}px;`;
                anchor.style.anchorName = `--tooltip-position-${crypto.randomUUID()}`;
                this.#positioningAnchor = anchor;
                this.appendChild(anchor);
                tooltip.style.positionAnchor = anchor.style.anchorName;
            }
            // The Fluent template sets popover=auto when connected. Switch afterwards so a
            // tooltip never light-dismisses an open menu or select popover.
            tooltip.setAttribute("popover", "manual");
            tooltip.showTooltip(0);
            const observe = current => {
                this.#observer.disconnect();
                this.#observer.observe(target.anchor, {
                    attributes: true,
                    attributeFilter: ["data-tooltip", "data-tooltip-content", "data-tooltip-cell", "data-tooltip-option"],
                    childList: true,
                    subtree: true
                });
                if (current.content) {
                    this.#observer.observe(current.content, {
                        attributes: true,
                        childList: true,
                        characterData: true,
                        subtree: true
                    });
                }
            };
            this.#observer = new MutationObserver(() => {
                const updated = this.#getTarget(target.anchor);
                if (target.anchor.isConnected && (updated?.text || updated?.content)) {
                    this.#setContent(tooltip, updated);
                    observe(updated);
                } else {
                    this.#clear();
                }
            });
            observe(current);
            // Observing the anchor itself for mutations doesn't detect its removal or an ancestor
            // becoming hidden, for example when a virtualized row or open menu disappears.
            this.#visibilityObserver = new IntersectionObserver(entries => {
                if (entries.some(entry => !entry.isIntersecting)) {
                    this.#clear();
                }
            });
            this.#visibilityObserver.observe(target.anchor);
        }, delay);
    }

    #setContent(tooltip, target) {
        tooltip.className = "dashboard-tooltip";
        if (target.content) {
            // Clone display-only markup so Blazor keeps ownership of the hidden source nodes.
            // Apply source classes to the popup too, so page-owned CSS controls its layout and width.
            tooltip.classList.add(...target.content.classList);
            const content = target.content.cloneNode(true);
            tooltip.replaceChildren(...content.childNodes);
        } else {
            const content = document.createElement("span");
            content.textContent = target.text;
            tooltip.replaceChildren(content);
        }
        this.#content = target;
    }

    #leave(element) {
        if (this.#anchor?.contains(element) || this.#tooltip?.contains(element) || this.#anchor?.matches(":hover")) {
            return;
        }

        this.#clear();
    }

    #clear() {
        this.#version++;
        this.#observer?.disconnect();
        this.#observer = null;
        this.#visibilityObserver?.disconnect();
        this.#visibilityObserver = null;
        clearTimeout(this.#timer);
        this.#timer = null;
        this.#tooltip?.remove();
        this.#tooltip?.anchorPositioningStyleElement?.remove();
        this.#tooltip = null;
        this.#content = null;
        this.#positioningAnchor?.remove();
        this.#positioningAnchor = null;
        this.#anchor = null;
    }
}

customElements.define("aspire-tooltip-provider", AspireTooltipProvider);

// FluentDataGrid's default header renders native titles internally, including on its sort
// buttons. Adapt only its header DOM rather than replacing the header template (which also
// owns sorting, column menus, keyboard shortcuts, and accessible sort descriptions).
// https://github.com/microsoft/fluentui-blazor/blob/dev/src/Core/Components/DataGrid/Columns/ColumnBase.razor
class AspireGridTooltips extends HTMLElement {
    #observer = null;

    connectedCallback() {
        queueMicrotask(() => {
            if (!this.isConnected) {
                return;
            }

            const grid = document.getElementById(this.getAttribute("grid"));
            if (!grid) {
                return;
            }

            const initialize = () => {
                const header = grid.querySelector("thead");
                if (!header) {
                    return false;
                }

                this.#observer?.disconnect();
                const observe = () => this.#observer.observe(header, {
                    attributes: true, attributeFilter: ["title"], childList: true, subtree: true
                });
                const convert = element => {
                    const text = element.getAttribute("title");
                    if (text) {
                        element.setAttribute("data-tooltip", text);
                    } else {
                        element.removeAttribute("data-tooltip");
                    }
                    element.removeAttribute("title");
                };
                this.#observer = new MutationObserver(records => {
                    // Disconnect while converting so our own title removals aren't interpreted
                    // as a later Blazor update that cleared the tooltip.
                    this.#observer.disconnect();
                    for (const record of records) {
                        if (record.type === "attributes") {
                            convert(record.target);
                        }
                    }
                    header.querySelectorAll("[title]").forEach(convert);
                    observe();
                });
                header.querySelectorAll("[title]").forEach(convert);
                observe();
                return true;
            };
            if (!initialize()) {
                // Columns can register after the grid first renders. Watch its content only until
                // the header exists, then limit observation to the header, not streaming rows.
                this.#observer = new MutationObserver(initialize);
                this.#observer.observe(grid, { childList: true, subtree: true });
            }
        });
    }

    disconnectedCallback() {
        this.#observer?.disconnect();
    }
}

customElements.define("aspire-grid-tooltips", AspireGridTooltips);
