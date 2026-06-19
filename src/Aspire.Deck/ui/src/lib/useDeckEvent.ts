import { useEffect, useRef, useState } from "react";
import {
  onConnection,
  onResources,
  onTelemetry,
} from "../api/deck";
import type {
  ConnectionState,
  ConnectionStatus,
  ConnectionTarget,
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
