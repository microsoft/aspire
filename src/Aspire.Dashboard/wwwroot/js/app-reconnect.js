// Tracks which mode the reconnect modal is currently in:
// "none" - modal is hidden
// "resource-service" - showing resource service connection failure (triggered by C# JS interop)
// "circuit" - showing Blazor circuit reconnection failure (triggered by framework event)
//
// Circuit mode always takes precedence over resource-service mode. When the circuit reconnects,
// the C# code will re-evaluate and re-trigger resource-service mode if still needed.
let currentMode = "none";

// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retryCircuit);

const resourceServiceRetryButton = document.getElementById("components-reconnect-resource-service-button");
resourceServiceRetryButton.addEventListener("click", retryResourceService);

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        // Blazor circuit disconnection always takes precedence.
        currentMode = "circuit";
        setModalClass("components-reconnect-show");
        if (!reconnectModal.open) {
            reconnectModal.showModal();
        }
    } else if (event.detail.state === "hide") {
        // Circuit reconnected. If resource service was also disconnected,
        // C# will re-trigger the resource-service modal via JS interop.
        currentMode = "none";
        setModalClass("");
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        currentMode = "circuit";
        setModalClass("components-reconnect-failed");
        document.addEventListener("visibilitychange", retryCircuitWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

// Called from C# JS interop when the resource service connection state changes.
// "disconnected" and "connected" are the two states. The initial "connecting" state
// does not show a modal — it's normal startup behavior, just like Blazor's circuit
// doesn't show a reconnect dialog on initial page load.
// The optional "show-retry" flag adds the retry button after repeated failures.
window.updateResourceServiceConnectionState = function (state, showRetry) {
    if (state === "disconnected") {
        // Only show resource service modal if we're not already in circuit mode.
        if (currentMode === "circuit") {
            return;
        }
        currentMode = "resource-service";
        var className = "components-reconnect-resource-service-disconnected";
        if (showRetry) {
            className += " components-reconnect-resource-service-show-retry";
        }
        setModalClass(className);
        if (!reconnectModal.open) {
            reconnectModal.showModal();
        }
    } else if (state === "connected") {
        // Only close if we're in resource-service mode, not circuit mode.
        if (currentMode === "resource-service") {
            currentMode = "none";
            setModalClass("");
            reconnectModal.close();
        }
    }
};

function setModalClass(className) {
    // Remove all state classes before applying the new one.
    reconnectModal.className = className;
}

async function retryCircuit() {
    document.removeEventListener("visibilitychange", retryCircuitWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            location.reload();
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryCircuitWhenDocumentBecomesVisible);
    }
}

// Called from C# to register the .NET object reference for callbacks.
window.registerResourceServiceConnectionProvider = function (dotNetRef) {
    window.resourceServiceConnectionProviderRef = dotNetRef;
};

function retryResourceService() {
    // Call the .NET method via JS interop reference set by ResourceServiceConnectionProvider.
    if (window.resourceServiceConnectionProviderRef) {
        window.resourceServiceConnectionProviderRef.invokeMethodAsync("ReconnectFromJs");
    }
}

async function retryCircuitWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retryCircuit();
    }
}
