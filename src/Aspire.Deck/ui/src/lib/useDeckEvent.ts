import { useEffect, useRef, useState } from "react";
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

// Subscribes to live resource events and maintains a name-keyed map, applying
// snapshots and incremental change deltas from the backend.
export function useResources(): { resources: Resource[]; ready: boolean } {
  const [resources, setResources] = useState<Resource[]>([]);
  const [ready, setReady] = useState(false);
  const mapRef = useRef<Map<string, Resource>>(new Map());

  useEffect(() => {
    const apply = (event: ResourcesEvent): void => {
      const map = mapRef.current;
      if (event.type === "snapshot") {
        map.clear();
        for (const resource of event.resources ?? []) {
          map.set(resource.name, resource);
        }
      } else {
        for (const resource of event.upserts ?? []) {
          map.set(resource.name, resource);
        }
        for (const name of event.deletes ?? []) {
          map.delete(name);
        }
      }
      setResources(Array.from(map.values()));
      setReady(true);
    };

    const unsubscribe = onResources(apply);
    return unsubscribe;
  }, []);

  return { resources, ready };
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
