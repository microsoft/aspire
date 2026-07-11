import type {
  AppHostInfo,
  CanvasManifest,
  CommandResponse,
  ConnectionStatus,
  ConnectionTarget,
  ConsoleLogEvent,
  DeckConfig,
  ExecuteCommandArgs,
  InteractionInfo,
  MetricSeriesQuery,
  MetricSeriesResponse,
  Resource,
  ResourcesEvent,
  TelemetrySummary,
} from "./types";

type Unsubscribe = () => void;

const connectionListeners = new Set<(status: ConnectionStatus) => void>();
const apphostListeners = new Set<(apphosts: AppHostInfo[]) => void>();
const connectionStatuses: Record<ConnectionTarget, ConnectionStatus> = {
  resourceService: { target: "resourceService", state: "connecting" },
  otlpGrpc: { target: "otlpGrpc", state: "disconnected" },
  otlpHttp: { target: "otlpHttp", state: "disconnected" },
};

const emptyTelemetry: TelemetrySummary = {
  logCount: 0,
  spanCount: 0,
  metricCount: 0,
  recentLogs: [],
  recentSpans: [],
  metrics: [],
};

let configPromise: Promise<DeckConfig> | null = null;

async function requestJson<T>(path: string): Promise<T> {
  const response = await fetch(`/api/deck/${path}`, {
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/json" },
  });
  if (!response.ok) {
    throw new Error(`Deck API request failed with ${response.status} ${response.statusText}.`);
  }

  return await response.json() as T;
}

async function postJson<TRequest, TResponse>(path: string, body: TRequest): Promise<TResponse> {
  const response = await fetch(`/api/deck/${path}`, {
    method: "POST",
    cache: "no-store",
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`Deck API request failed with ${response.status} ${response.statusText}.`);
  }

  return await response.json() as TResponse;
}

function getConfig(): Promise<DeckConfig> {
  if (configPromise === null) {
    const request = requestJson<DeckConfig>("config");
    configPromise = request;
    void request.catch(() => {
      if (configPromise === request) {
        configPromise = null;
      }
    });
  }
  return configPromise;
}

async function listResources(): Promise<Resource[]> {
  return await requestJson<Resource[]>("resources");
}

function toApphost(config: DeckConfig): AppHostInfo {
  return {
    id: "dashboard",
    name: config.applicationName ?? "Aspire",
    resourceServiceUrl: config.resourceServiceUrl ?? window.location.origin,
    state: connectionStatuses.resourceService.state,
    active: true,
  };
}

function setResourceConnection(status: ConnectionStatus): void {
  connectionStatuses.resourceService = status;
  for (const listener of connectionListeners) {
    listener(status);
  }
  void getConfig().then((config) => {
    const apphosts = [toApphost(config)];
    for (const listener of apphostListeners) {
      listener(apphosts);
    }
  }).catch(() => undefined);
}

function onResources(callback: (event: ResourcesEvent) => void): Unsubscribe {
  let cancelled = false;
  let timer: number | undefined;

  const poll = async (): Promise<void> => {
    try {
      const resources = await listResources();
      if (!cancelled) {
        callback({ type: "snapshot", resources });
        setResourceConnection({ target: "resourceService", state: "connected" });
      }
    } catch (error) {
      if (!cancelled) {
        setResourceConnection({
          target: "resourceService",
          state: "error",
          message: error instanceof Error ? error.message : String(error),
        });
      }
    } finally {
      if (!cancelled) {
        timer = window.setTimeout(() => void poll(), 1_000);
      }
    }
  };

  void poll();
  return () => {
    cancelled = true;
    if (timer !== undefined) {
      window.clearTimeout(timer);
    }
  };
}

function onConnection(callback: (status: ConnectionStatus) => void): Unsubscribe {
  connectionListeners.add(callback);
  for (const status of Object.values(connectionStatuses)) {
    callback(status);
  }
  return () => connectionListeners.delete(callback);
}

function listApphosts(): Promise<AppHostInfo[]> {
  return getConfig().then((config) => [toApphost(config)]);
}

function onApphosts(callback: (apphosts: AppHostInfo[]) => void): Unsubscribe {
  apphostListeners.add(callback);
  void listApphosts().then(callback).catch((error) => {
    setResourceConnection({
      target: "resourceService",
      state: "error",
      message: error instanceof Error ? error.message : String(error),
    });
  });
  return () => apphostListeners.delete(callback);
}

export const httpBackend = {
  getConfig,
  listResources,
  listCanvases(): Promise<CanvasManifest[]> {
    return Promise.resolve([]);
  },
  listApphosts,
  selectApphost(id: string): Promise<void> {
    return id === "dashboard"
      ? Promise.resolve()
      : Promise.reject(new Error(`Unknown dashboard AppHost '${id}'.`));
  },
  onApphosts,
  onInteractions(callback: (interactions: InteractionInfo[]) => void): Unsubscribe {
    callback([]);
    return () => undefined;
  },
  respondInteraction(_interactionId: number, _action: string, _values: Record<string, string>): void {
  },
  getTelemetrySummary(): Promise<TelemetrySummary> {
    return Promise.resolve(emptyTelemetry);
  },
  getMetricSeries(_query: MetricSeriesQuery): Promise<MetricSeriesResponse | null> {
    return Promise.resolve(null);
  },
  executeCommand(args: ExecuteCommandArgs): Promise<CommandResponse> {
    return postJson("commands/execute", {
      resourceName: args.resourceName,
      commandName: args.commandName,
    });
  },
  subscribeConsoleLogs(_resourceName: string, _callback: (event: ConsoleLogEvent) => void): Unsubscribe {
    return () => undefined;
  },
  onResources,
  onConnection,
  onTelemetry(callback: (summary: TelemetrySummary) => void): Unsubscribe {
    callback(emptyTelemetry);
    return () => undefined;
  },
};
