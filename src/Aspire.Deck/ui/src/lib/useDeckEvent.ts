import { useEffect, useState } from "react";
import {
  onApphosts,
  onConnection,
  onInteractions,
  onResources,
  onTelemetry,
} from "../api/deck";
import type {
  AppHostInfo,
  ConnectionState,
  ConnectionStatus,
  ConnectionTarget,
  InteractionInfo,
  Resource,
  ResourcesEvent,
  TelemetrySummary,
} from "../api/types";

type ConnectionMap = Record<ConnectionTarget, ConnectionState>;

const INITIAL_CONNECTION: ConnectionMap = {
  resourceService: "connecting",
  otlpGrpc: "connecting",
  otlpHttp: "connecting",
};

interface ResourceStoreSnapshot {
  resources: Resource[];
  ready: boolean;
}

const resourceMap = new Map<string, Resource>();
const resourceListeners = new Set<() => void>();
let resourceSubscription: (() => void) | null = null;
let resourceSubscriptionTimer: number | undefined;
let resourceSnapshot: ResourceStoreSnapshot = { resources: [], ready: false };

function applyResourceEvent(event: ResourcesEvent): void {
  if (event.type === "snapshot") {
    resourceMap.clear();
    for (const resource of event.resources ?? []) {
      resourceMap.set(resource.name, resource);
    }
  } else {
    for (const resource of event.upserts ?? []) {
      resourceMap.set(resource.name, resource);
    }
    for (const name of event.deletes ?? []) {
      resourceMap.delete(name);
    }
  }

  resourceSnapshot = { resources: Array.from(resourceMap.values()), ready: true };
  for (const listener of resourceListeners) {
    listener();
  }
}

function subscribeResourceStore(listener: () => void): () => void {
  resourceListeners.add(listener);
  // Several mounted pages consume the resource inventory at once. Keep one backend
  // subscription so HTTP polling and SignalR connections are not multiplied per hook.
  // Deferring its creation also lets React Strict Mode finish its synthetic unmount/remount
  // without starting a request that it immediately has to abort.
  if (resourceSubscription === null && resourceSubscriptionTimer === undefined) {
    resourceSubscriptionTimer = window.setTimeout(() => {
      resourceSubscriptionTimer = undefined;
      if (resourceListeners.size > 0 && resourceSubscription === null) {
        resourceSubscription = onResources(applyResourceEvent);
      }
    });
  }

  return () => {
    resourceListeners.delete(listener);
    if (resourceListeners.size === 0) {
      if (resourceSubscriptionTimer !== undefined) {
        window.clearTimeout(resourceSubscriptionTimer);
        resourceSubscriptionTimer = undefined;
      }
      resourceSubscription?.();
      resourceSubscription = null;
      resourceMap.clear();
      resourceSnapshot = { resources: [], ready: false };
    }
  };
}

function getResourceSnapshot(): ResourceStoreSnapshot {
  return resourceSnapshot;
}

// Shares one live backend subscription while retaining hook-local snapshots. Priming each
// consumer on the next task preserves the existing effect ordering for pages that open
// additional streams after the resource inventory becomes available.
export function useResources(): ResourceStoreSnapshot {
  const [snapshot, setSnapshot] = useState<ResourceStoreSnapshot>({ resources: [], ready: false });

  useEffect(() => {
    const update = (): void => setSnapshot(getResourceSnapshot());
    const unsubscribe = subscribeResourceStore(update);
    const primeTimer = window.setTimeout(update);
    return () => {
      window.clearTimeout(primeTimer);
      unsubscribe();
    };
  }, []);

  return snapshot;
}

export function useConnection(): ConnectionMap {
  const [connection, setConnection] = useState<ConnectionMap>(INITIAL_CONNECTION);

  useEffect(() => {
    const unsubscribe = onConnection((status: ConnectionStatus) => {
      setConnection((prev) => ({ ...prev, [status.target]: status.state }));
    });
    return unsubscribe;
  }, []);

  return connection;
}

export function useTelemetry(): TelemetrySummary | null {
  const [summary, setSummary] = useState<TelemetrySummary | null>(null);

  useEffect(() => {
    const unsubscribe = onTelemetry(setSummary);
    return unsubscribe;
  }, []);

  return summary;
}

// Tracks the set of attached AppHosts (for the switcher). The list updates live as
// AppHosts attach/detach or their connection state changes.
export function useApphosts(): AppHostInfo[] {
  const [apphosts, setApphosts] = useState<AppHostInfo[]>([]);

  useEffect(() => {
    const unsubscribe = onApphosts(setApphosts);
    return unsubscribe;
  }, []);

  return apphosts;
}

// Tracks the active AppHost's open interactions (command inputs, message boxes,
// notifications). Empty when there are none. The backend sends the full list on
// every change, so the UI can simply replace its state.
export function useInteractions(): InteractionInfo[] {
  const [interactions, setInteractions] = useState<InteractionInfo[]>([]);

  useEffect(() => {
    const unsubscribe = onInteractions(setInteractions);
    return unsubscribe;
  }, []);

  return interactions;
}
