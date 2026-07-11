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
  LogRecordSummary,
  MetricSeriesQuery,
  MetricSeriesResponse,
  Resource,
  ResourcesEvent,
  TelemetrySummary,
} from "./types";
import { readNdjson } from "./ndjson";
import {
  getLogRecordSummaries,
  type OtlpLogRecordSummary,
  type OtlpTelemetryData,
  type TelemetryApiResponse,
} from "./otlp";

type Unsubscribe = () => void;

const connectionListeners = new Set<(status: ConnectionStatus) => void>();
const apphostListeners = new Set<(apphosts: AppHostInfo[]) => void>();
const telemetryListeners = new Set<(summary: TelemetrySummary) => void>();
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

let telemetrySummary: TelemetrySummary = { ...emptyTelemetry, recentLogs: [] };
let telemetryController: AbortController | null = null;
let telemetryStarted = false;
let telemetryStopTimer: number | undefined;
const telemetryLogKeys = new Set<string>();

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

async function postNoContent<TRequest>(path: string, body: TRequest): Promise<void> {
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

  await response.arrayBuffer();
}

async function deleteNoContent(path: string): Promise<void> {
  const response = await fetch(`/api/deck/${path}`, {
    method: "DELETE",
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/json" },
  });
  if (!response.ok) {
    throw new Error(`Deck API request failed with ${response.status} ${response.statusText}.`);
  }

  await response.arrayBuffer();
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

function onInteractions(callback: (interactions: InteractionInfo[]) => void): Unsubscribe {
  let cancelled = false;
  let timer: number | undefined;

  const poll = async (): Promise<void> => {
    try {
      const interactions = await requestJson<InteractionInfo[]>("interactions");
      if (!cancelled) {
        callback(interactions);
      }
    } catch {
      // Keep the last snapshot during a transient failure. Resource polling owns the
      // connection indicator and will surface a dashboard backend outage.
    } finally {
      if (!cancelled) {
        timer = window.setTimeout(() => void poll(), 250);
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

async function streamConsoleLogs(
  resourceName: string,
  callback: (event: ConsoleLogEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  const response = await fetch(
    `/api/deck/resources/${encodeURIComponent(resourceName)}/console-logs`,
    {
      cache: "no-store",
      credentials: "same-origin",
      headers: { Accept: "application/x-ndjson" },
      signal,
    },
  );
  if (!response.ok) {
    throw new Error(`Console log stream failed with ${response.status} ${response.statusText}.`);
  }
  if (response.body === null) {
    throw new Error("Console log stream returned no response body.");
  }

  await readNdjson<ConsoleLogEvent>(response.body, callback);
}

async function streamStructuredLogs(
  callback: (logs: OtlpLogRecordSummary[]) => void,
  signal: AbortSignal,
): Promise<void> {
  const response = await fetch("/api/deck/telemetry/logs?follow=true", {
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/x-ndjson" },
    signal,
  });
  if (!response.ok) {
    throw new Error(`Structured log stream failed with ${response.status} ${response.statusText}.`);
  }
  if (response.body === null) {
    throw new Error("Structured log stream returned no response body.");
  }

  await readNdjson<OtlpTelemetryData>(response.body, (data) => callback(getLogRecordSummaries(data)));
}

function compareNewestFirst(left: LogRecordSummary, right: LogRecordSummary): number {
  if (left.timeUnixNano.length !== right.timeUnixNano.length) {
    return right.timeUnixNano.length - left.timeUnixNano.length;
  }
  return right.timeUnixNano.localeCompare(left.timeUnixNano);
}

function toLogRecordSummary(log: OtlpLogRecordSummary): LogRecordSummary {
  const { recordKey: _, ...summary } = log;
  return summary;
}

function notifyTelemetry(): void {
  for (const listener of telemetryListeners) {
    listener(telemetrySummary);
  }
}

function appendStructuredLogs(logs: OtlpLogRecordSummary[]): void {
  const additions = logs.filter((log) => {
    if (telemetryLogKeys.has(log.recordKey)) {
      return false;
    }
    telemetryLogKeys.add(log.recordKey);
    return true;
  });
  if (additions.length === 0) {
    return;
  }

  telemetrySummary = {
    ...telemetrySummary,
    logCount: telemetrySummary.logCount + additions.length,
    recentLogs: [...additions.map(toLogRecordSummary), ...telemetrySummary.recentLogs]
      .sort(compareNewestFirst)
      .slice(0, 200),
  };
  notifyTelemetry();
}

function summaryFromStructuredLogs(response: TelemetryApiResponse): TelemetrySummary {
  return {
    ...emptyTelemetry,
    logCount: response.totalCount,
    recentLogs: getLogRecordSummaries(response.data)
      .map(toLogRecordSummary)
      .sort(compareNewestFirst),
  };
}

async function clearStructuredLogs(resourceName: string | null): Promise<void> {
  const resourceQuery = resourceName === null ? "" : `?resource=${encodeURIComponent(resourceName)}`;
  await deleteNoContent(`telemetry/logs${resourceQuery}`);

  // The live NDJSON stream only carries additions. Refresh after a destructive
  // mutation so local totals and dedupe keys exactly match the server snapshot.
  const response = await requestJson<TelemetryApiResponse>("telemetry/logs?limit=200");
  const records = getLogRecordSummaries(response.data);
  telemetryLogKeys.clear();
  for (const record of records) {
    telemetryLogKeys.add(record.recordKey);
  }
  telemetrySummary = {
    ...telemetrySummary,
    logCount: response.totalCount,
    recentLogs: records.map(toLogRecordSummary).sort(compareNewestFirst),
  };
  notifyTelemetry();
}

function ensureTelemetryStream(): void {
  if (telemetryStarted) {
    return;
  }

  telemetryStarted = true;
  const controller = new AbortController();
  telemetryController = controller;
  void streamStructuredLogs(appendStructuredLogs, controller.signal).catch(() => {
    // Resource polling owns the shared backend connection state. Preserve the
    // last telemetry snapshot when a live stream ends or the backend is unavailable.
  });
}

function subscribeTelemetry(callback: (summary: TelemetrySummary) => void): Unsubscribe {
  if (telemetryStopTimer !== undefined) {
    window.clearTimeout(telemetryStopTimer);
    telemetryStopTimer = undefined;
  }

  telemetryListeners.add(callback);
  callback(telemetrySummary);
  ensureTelemetryStream();

  return () => {
    telemetryListeners.delete(callback);
    if (telemetryListeners.size === 0) {
      // React development mode immediately remounts effects. Deferring teardown
      // keeps that lifecycle probe from opening and aborting duplicate HTTP streams.
      telemetryStopTimer = window.setTimeout(() => {
        telemetryStopTimer = undefined;
        if (telemetryListeners.size !== 0) {
          return;
        }
        telemetryController?.abort();
        telemetryController = null;
        telemetryStarted = false;
        telemetrySummary = { ...emptyTelemetry, recentLogs: [] };
        telemetryLogKeys.clear();
      });
    }
  };
}

function subscribeConsoleLogs(
  resourceName: string,
  callback: (event: ConsoleLogEvent) => void,
): Unsubscribe {
  const controller = new AbortController();
  void streamConsoleLogs(resourceName, callback, controller.signal).catch((error: unknown) => {
    if (!controller.signal.aborted) {
      console.error(`Console log stream for '${resourceName}' failed.`, error);
    }
  });

  return () => controller.abort();
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
  onInteractions,
  respondInteraction(interactionId: number, action: string, values: Record<string, string>): void {
    void postNoContent("interactions/respond", { interactionId, action, values }).catch(() => undefined);
  },
  getTelemetrySummary(): Promise<TelemetrySummary> {
    return requestJson<TelemetryApiResponse>("telemetry/logs?limit=200").then(summaryFromStructuredLogs);
  },
  clearStructuredLogs,
  getMetricSeries(_query: MetricSeriesQuery): Promise<MetricSeriesResponse | null> {
    return Promise.resolve(null);
  },
  executeCommand(args: ExecuteCommandArgs): Promise<CommandResponse> {
    return postJson("commands/execute", {
      resourceName: args.resourceName,
      commandName: args.commandName,
    });
  },
  subscribeConsoleLogs,
  onResources,
  onConnection,
  onTelemetry(callback: (summary: TelemetrySummary) => void): Unsubscribe {
    return subscribeTelemetry(callback);
  },
};
