export const InputRoute = Object.freeze({
    Continue: "continue", Consume: "consume", Application: "application", Browser: "browser"
});
export const TerminalAction = Object.freeze({
    CopySelection: "copySelection", PasteClipboard: "pasteClipboard", CopyOrPaste: "copyOrPaste",
    ClearSelection: "clearSelection", ScrollToLive: "scrollToLive", ScrollLines: "scrollLines"
});
const browserCtrlKeys = new Set(["l", "t", "v", "w", "+", "-", "=", "0"]);
const browserFunctionKeys = new Set(["F1", "F3", "F5", "F6", "F7", "F10", "F11", "F12"]);
const terminalKeys = new Set(["Tab", "Enter", "Backspace", "Delete", "Escape", "ArrowUp", "ArrowDown",
    "ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown", "Insert",
    ...Array.from({ length: 12 }, (_, index) => `F${index + 1}`)]);
const routes = new Set(Object.values(InputRoute));
/** Fresh, inspectable defaults. Overrides are per mounted view, never global. */
export function defaultInputBindings() {
    return [
        {
            id: "clipboard.copy-key",
            match: input => input.type === "key" && input.key.toLowerCase() === "c" && !input.alt &&
                ((input.meta && !input.ctrl) || (input.ctrl && input.shift && !input.meta)),
            action: TerminalAction.CopySelection
        },
        {
            id: "browser.shortcuts",
            match: input => input.type === "key" && (input.meta || browserFunctionKeys.has(input.key) ||
                (input.ctrl && (input.key === "Tab" || input.key === "F4" || browserCtrlKeys.has(input.key.toLowerCase()) ||
                    (input.shift && input.key.length === 1))) || (input.alt && !input.ctrl)),
            route: InputRoute.Browser
        },
        {
            id: "terminal.keys",
            match: input => input.type === "key" && (terminalKeys.has(input.key) || (input.ctrl && input.key.length === 1)),
            route: InputRoute.Application
        },
        {
            id: "clipboard.context-click",
            match: (input, context) => input.type === "pointer" && input.button === "right" && !input.meta &&
                (!context.mouseCaptured || input.shift || context.historical || context.readOnly),
            action: TerminalAction.CopyOrPaste
        }
    ];
}
function synchronous(callback, name) {
    if (typeof callback !== "function" || callback.constructor.name === "AsyncFunction")
        throw new TypeError(`${name} must be a synchronous function`);
}
/** Resolves intent only. DOM cancellation and action execution belong to the mounted view. */
export class InputPolicy {
    #bindings;
    #intercept;
    #actions;
    constructor({ inputBindings = [], onInput, actions = {} } = {}) {
        if (!Array.isArray(inputBindings))
            throw new TypeError("inputBindings must be an array");
        if (!actions || typeof actions !== "object" || Array.isArray(actions))
            throw new TypeError("actions must be an object");
        this.#actions = new Set(Object.values(TerminalAction));
        for (const [name, handler] of Object.entries(actions)) {
            if (!name || this.#actions.has(name) || typeof handler !== "function")
                throw new TypeError(`Invalid or reserved action: ${name}`);
            this.#actions.add(name);
        }
        if (onInput !== undefined)
            synchronous(onInput, "onInput");
        this.#intercept = onInput;
        const defaults = defaultInputBindings();
        const ids = new Set();
        const overrides = inputBindings.map((binding) => {
            if (!binding || typeof binding.id !== "string" || !binding.id || ids.has(binding.id))
                throw new TypeError("Each input binding needs a unique, nonempty id");
            ids.add(binding.id);
            if (binding.remove === true) {
                if (!defaults.some(item => item.id === binding.id))
                    throw new TypeError(`Cannot remove unknown default binding: ${binding.id}`);
                if (Object.keys(binding).some(key => !["id", "remove"].includes(key)))
                    throw new TypeError(`Removed binding ${binding.id} must contain only id and remove`);
                return null;
            }
            synchronous(binding.match, `Binding ${binding.id}.match`);
            if (binding.when !== undefined)
                synchronous(binding.when, `Binding ${binding.id}.when`);
            this.#decision(binding);
            return Object.freeze({ ...binding });
        }).filter((binding) => binding !== null);
        this.#bindings = [...overrides, ...defaults.filter(binding => !ids.has(binding.id))];
    }
    get bindings() { return this.#bindings.map(binding => ({ ...binding })); }
    #decision(value) {
        if (isRoute(value))
            return { route: value };
        if (!isRecord(value) || typeof value.then === "function")
            throw new TypeError("Input routing must synchronously return a route or action");
        const hasAction = Object.hasOwn(value, "action");
        const hasRoute = Object.hasOwn(value, "route");
        if (hasAction === hasRoute)
            throw new TypeError("Specify exactly one action or route");
        if (hasRoute) {
            if (!isRoute(value.route))
                throw new TypeError(`Unknown input route: ${String(value.route)}`);
            return { route: value.route };
        }
        if (!isActionHandler(value.action) && (typeof value.action !== "string" || !this.#actions.has(value.action)))
            throw new TypeError(`Unknown terminal action: ${String(value.action)}`);
        return { action: value.action, args: value.args };
    }
    resolve(input, context) {
        const intercepted = this.#intercept?.(input, context);
        if (intercepted !== undefined) {
            const decision = this.#decision(intercepted);
            if (decision.route !== InputRoute.Continue)
                return decision;
        }
        for (const binding of this.#bindings) {
            const matched = binding.match(input, context);
            if (typeof matched !== "boolean")
                throw new TypeError(`Binding ${binding.id}.match must return a boolean`);
            if (!matched)
                continue;
            if (binding.when) {
                const enabled = binding.when(context, input);
                if (typeof enabled !== "boolean")
                    throw new TypeError(`Binding ${binding.id}.when must return a boolean`);
                if (!enabled)
                    continue;
            }
            const decision = this.#decision(binding);
            if (decision.route !== InputRoute.Continue)
                return decision;
        }
        return { route: InputRoute.Continue };
    }
}
function isRoute(value) {
    return typeof value === "string" && [...routes].some(route => route === value);
}
function isActionHandler(value) {
    return typeof value === "function";
}
export function inputModifiers(event) {
    return { ctrl: !!event.ctrlKey, alt: !!event.altKey, shift: !!event.shiftKey, meta: !!event.metaKey };
}
import { isRecord } from "./validation.js";
//# sourceMappingURL=input-policy.js.map