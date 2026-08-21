// Fluent UI Blazor 5.0.0-rc.5 registers custom events from both Blazor Web and Server
// startup hooks, and registers overflowchange with an identically named browser event.
// .NET 11 rejects both patterns. Preserve the event converter through a synthetic browser
// event and ignore duplicate registrations until a Fluent package with the fixes is available.
// See https://github.com/microsoft/fluentui-blazor/issues/4626.
const registeredEventTypes = new Set();
const bridgedBrowserEvents = new Set();
const registerCustomEventType = Blazor.registerCustomEventType.bind(Blazor);
const showPopover = HTMLElement.prototype.showPopover;

// RC5 tooltips can outlive a dialog by their configured delay and then call showPopover()
// after the tooltip has been disconnected. Chromium rejects that invalid state.
HTMLElement.prototype.showPopover = function (...args) {
    if (!this.isConnected) {
        return;
    }

    return showPopover.apply(this, args);
};

Blazor.registerCustomEventType = (eventName, options) => {
    if (registeredEventTypes.has(eventName)) {
        return;
    }

    registeredEventTypes.add(eventName);

    if (eventName !== options.browserEventName) {
        registerCustomEventType(eventName, options);
        return;
    }

    const browserEventName = `__fluent_${eventName}`;
    if (!bridgedBrowserEvents.has(eventName)) {
        bridgedBrowserEvents.add(eventName);
        document.addEventListener(eventName, event => {
            if (event.target instanceof EventTarget) {
                event.target.dispatchEvent(new CustomEvent(browserEventName, {
                    bubbles: event.bubbles,
                    cancelable: event.cancelable,
                    composed: event.composed,
                    detail: event.detail
                }));
            }
        }, true);
    }

    registerCustomEventType(eventName, { ...options, browserEventName });
};

Blazor.start();
