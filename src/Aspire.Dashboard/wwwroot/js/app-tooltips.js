// Share one on-demand Fluent tooltip across text-only anchors. Creating a Blazor tooltip per
// log entry or grid cell would add thousands of components to high-throughput telemetry views.
class AspireTooltipProvider extends HTMLElement {
    #controller = null;
    #tooltip = null;
    #anchor = null;
    #timer = null;
    #version = 0;
    #observer = null;
    #visibilityObserver = null;

    connectedCallback() {
        this.#controller = new AbortController();
        const options = { signal: this.#controller.signal };
        document.addEventListener("mouseover", event => this.#show(event.target, 250), options);
        document.addEventListener("focusin", event => this.#show(event.target, 0), options);
        document.addEventListener("mouseout", event => this.#leave(event.relatedTarget), options);
        document.addEventListener("focusout", event => this.#leave(event.relatedTarget), options);
        document.addEventListener("pointerdown", () => this.#clear(), options);
        document.addEventListener("keydown", event => {
            if (event.key === "Escape") {
                this.#clear();
            }
        }, options);
        document.addEventListener("scroll", () => this.#clear(), { ...options, capture: true });
    }

    disconnectedCallback() {
        this.#controller.abort();
        this.#clear();
    }

    #getTarget(element) {
        if (!(element instanceof Element)) {
            return null;
        }

        const anchor = element.closest("[data-tooltip]");
        if (anchor) {
            return { anchor, text: anchor.getAttribute("data-tooltip") };
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
        if (!target?.text) {
            this.#clear();
            return;
        }

        if (this.#anchor === target.anchor && this.#tooltip?.textContent === target.text) {
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
            if (!current?.text) {
                this.#clear();
                return;
            }

            target.anchor.id ||= `tooltip-anchor-${crypto.randomUUID()}`;
            const tooltip = document.createElement("fluent-tooltip");
            tooltip.setAttribute("anchor", target.anchor.id);
            let positioning = "above";
            if (target.anchor.matches("fluent-menu-item")) {
                const bounds = target.anchor.getBoundingClientRect();
                // Keep descriptions beside menus when there is room for Fluent's default 240px
                // maximum width. Full-width mobile menus need above-anchor placement instead.
                if (Math.max(bounds.left, window.innerWidth - bounds.right) >= 240) {
                    positioning = "before";
                }
            }
            tooltip.setAttribute("positioning", positioning);
            tooltip.className = "dashboard-tooltip";
            const content = document.createElement("span");
            content.textContent = current.text;
            tooltip.appendChild(content);
            this.#tooltip = tooltip;
            this.appendChild(tooltip);
            // The Fluent template sets popover=auto when connected. Switch afterwards so a
            // tooltip never light-dismisses an open menu or select popover.
            tooltip.setAttribute("popover", "manual");
            tooltip.showTooltip(0);
            this.#observer = new MutationObserver(() => {
                const updated = this.#getTarget(target.anchor);
                if (target.anchor.isConnected && updated?.text) {
                    content.textContent = updated.text;
                } else {
                    this.#clear();
                }
            });
            this.#observer.observe(target.anchor, {
                attributes: true,
                attributeFilter: ["data-tooltip", "data-tooltip-cell", "data-tooltip-option"],
                childList: true,
                subtree: true
            });
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
