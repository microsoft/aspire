import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
  type ISubscription,
} from "@microsoft/signalr";
import type {
  ConnectionStatus,
  ConsoleLogEvent,
  DashboardApiDiscovery,
  DashboardApiVersion,
  DashboardConfiguration,
  DashboardStructuredLogsEvent,
  DashboardStructuredLogsSnapshot,
  DeckConfig,
  CommandResponse,
  ExecuteCommandArgs,
  Resource,
  ResourcesEvent,
  LogRecordSummary,
} from "./types";
import { getLogRecordSummaries, type OtlpLogRecordSummary } from "./otlp";

const dashboardProduct = "Aspire.Dashboard";
const discoveryPath = "/api/dashboard";
const configurationCapability = "configuration";
const resourcesCapability = "resources";
const resourceStreamCapability = "resources-live";
const commandsCapability = "commands";
const structuredLogsCapability = "structured-logs";
const structuredLogStreamCapability = "structured-logs-live";
const consoleLogsCapability = "console-logs";
const consoleLogStreamCapability = "console-logs-live";
const supportedVersions = new Set([1]);

let negotiatedVersion: Promise<DashboardApiVersion> | null = null;
let configuration: Promise<DeckConfig> | null = null;
const structuredLogListeners = new Set<(logs: NativeStructuredLogs) => void>();
const structuredLogKeys = new Set<string>();
let structuredLogs: NativeStructuredLogs = { logCount: 0, recentLogs: [] };

interface NativeStructuredLogs {
  logCount: number;
  recentLogs: LogRecordSummary[];
}

function compareNewestFirst(left: LogRecordSummary, right: LogRecordSummary): number {
  if (left.timeUnixNano.length !== right.timeUnixNano.length) {
    return right.timeUnixNano.length - left.timeUnixNano.length;
  }
  return right.timeUnixNano.localeCompare(left.timeUnixNano);
}

function withoutRecordKey(log: OtlpLogRecordSummary): LogRecordSummary {
  const { recordKey: _, ...summary } = log;
  return summary;
}

function notifyStructuredLogs(): void {
  for (const listener of structuredLogListeners) listener(structuredLogs);
}

async function refreshStructuredLogs(): Promise<void> {
  const version = await getNegotiatedVersion();
  const snapshot = await requestJson(`${version.basePath}/structured-logs`) as DashboardStructuredLogsSnapshot;
  if (!Number.isInteger(snapshot.totalCount) || typeof snapshot.data !== "object" || snapshot.data === null) {
    throw new Error("Dashboard API structured-log snapshot returned an incompatible payload.");
  }
  const records = getLogRecordSummaries(snapshot.data);
  structuredLogKeys.clear();
  for (const record of records) structuredLogKeys.add(record.recordKey);
  structuredLogs = {
    logCount: snapshot.totalCount,
    recentLogs: records.map(withoutRecordKey).sort(compareNewestFirst),
  };
  notifyStructuredLogs();
}

function appendStructuredLogEvent(event: DashboardStructuredLogsEvent): void {
  const additions = getLogRecordSummaries(event.data).filter((log) => {
    if (structuredLogKeys.has(log.recordKey)) return false;
    structuredLogKeys.add(log.recordKey);
    return true;
  }).map(withoutRecordKey);
  if (additions.length === 0) return;
  structuredLogs = {
    logCount: structuredLogs.logCount + additions.length,
    recentLogs: [...additions, ...structuredLogs.recentLogs].sort(compareNewestFirst).slice(0, 5_000),
  };
  notifyStructuredLogs();
}

async function requestJson(path: string): Promise<unknown> {
  const response = await fetch(path, {
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/json" },
  });
  if (!response.ok) {
    throw new Error(`Dashboard API request failed with ${response.status} ${response.statusText}.`);
  }

  return await response.json() as unknown;
}

async function postJson(path: string, body: unknown): Promise<unknown> {
  const response = await fetch(path, {
    method: "POST",
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`Dashboard API request failed with ${response.status} ${response.statusText}.`);
  }

  return await response.json() as unknown;
}

function isVersion(value: unknown): value is DashboardApiVersion {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const candidate = value as Partial<DashboardApiVersion>;
  return Number.isInteger(candidate.version)
    && typeof candidate.basePath === "string"
    && Array.isArray(candidate.capabilities)
    && candidate.capabilities.every((capability) => typeof capability === "string");
}

function validateBasePath(basePath: string): string {
  const url = new URL(basePath, window.location.origin);
  if (url.origin !== window.location.origin
      || !url.pathname.startsWith(`${discoveryPath}/`)
      || url.search !== ""
      || url.hash !== "") {
    throw new Error(`Dashboard API returned an invalid version base path: ${basePath}.`);
  }

  return url.pathname.replace(/\/$/, "");
}

async function negotiateVersion(): Promise<DashboardApiVersion> {
  const payload = await requestJson(discoveryPath) as Partial<DashboardApiDiscovery>;
  if (payload.product !== dashboardProduct || !Array.isArray(payload.versions)) {
    throw new Error("Dashboard API discovery returned an incompatible product or payload.");
  }

  const version = payload.versions
    .filter(isVersion)
    .filter((candidate) => supportedVersions.has(candidate.version))
    .filter((candidate) => candidate.capabilities.includes(configurationCapability))
    .sort((left, right) => right.version - left.version)[0];
  if (version === undefined) {
    const advertised = payload.versions
      .filter(isVersion)
      .map((candidate) => candidate.version)
      .sort((left, right) => right - left)
      .join(", ") || "none";
    throw new Error(`Dashboard API has no compatible configuration version (server: ${advertised}; client: 1).`);
  }

  return { ...version, basePath: validateBasePath(version.basePath) };
}

function getNegotiatedVersion(): Promise<DashboardApiVersion> {
  if (negotiatedVersion === null) {
    const request = negotiateVersion();
    negotiatedVersion = request;
    void request.catch(() => {
      if (negotiatedVersion === request) {
        negotiatedVersion = null;
      }
    });
  }

  return negotiatedVersion;
}

async function loadConfig(): Promise<DeckConfig> {
  const version = await getNegotiatedVersion();
  const payload = await requestJson(`${version.basePath}/config`) as Partial<DashboardConfiguration>;
  if (typeof payload.applicationName !== "string"
      || typeof payload.dashboardVersion !== "string"
      || typeof payload.runtimeVersion !== "string") {
    throw new Error("Dashboard API configuration returned an incompatible payload.");
  }

  return {
    applicationName: payload.applicationName,
    resourceServiceUrl: null,
    otlpGrpcUrl: null,
    otlpHttpUrl: null,
    version: payload.dashboardVersion,
    runtimeVersion: payload.runtimeVersion,
  };
}

function getConfig(): Promise<DeckConfig> {
  if (configuration === null) {
    const request = loadConfig();
    configuration = request;
    void request.catch(() => {
      if (configuration === request) {
        configuration = null;
      }
    });
  }

  return configuration;
}

async function hasCapability(capability: string): Promise<boolean> {
  return (await getNegotiatedVersion()).capabilities.includes(capability);
}

async function listResources(): Promise<Resource[]> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(resourcesCapability)) {
    throw new Error("Dashboard API version 1 does not advertise the resources capability.");
  }

  const payload = await requestJson(`${version.basePath}/resources`);
  if (!Array.isArray(payload)) {
    throw new Error("Dashboard API resources returned an incompatible payload.");
  }

  return payload as Resource[];
}

async function executeCommand(args: ExecuteCommandArgs): Promise<CommandResponse> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(commandsCapability)) {
    throw new Error("Dashboard API version 1 does not advertise the commands capability.");
  }

  return await postJson(`${version.basePath}/commands/execute`, {
    resourceName: args.resourceName,
    commandName: args.commandName,
  }) as CommandResponse;
}

function isResourcesEvent(value: unknown): value is ResourcesEvent {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const candidate = value as Partial<ResourcesEvent>;
  return candidate.type === "snapshot"
    ? Array.isArray(candidate.resources)
    : candidate.type === "change"
      && Array.isArray(candidate.upserts)
      && Array.isArray(candidate.deletes);
}

function subscribeResources(
  callback: (event: ResourcesEvent) => void,
  reportConnection: (status: ConnectionStatus) => void,
  registerRetry: (retry: (() => void) | null) => void,
): () => void {
  let cancelled = false;
  let starting = false;
  let retryTimer: number | undefined;
  let connection: HubConnection | null = null;
  let streamSubscription: ISubscription<ResourcesEvent> | null = null;
  let connectionFailed = false;

  const reportError = (error: unknown): void => {
    connectionFailed = true;
    reportConnection({
      target: "resourceService",
      state: "error",
      message: error instanceof Error ? error.message : String(error),
    });
  };

  const stopForStreamError = (error: unknown): void => {
    if (cancelled) {
      return;
    }

    reportError(error);
    if (connection?.state === HubConnectionState.Connected) {
      void connection.stop();
    }
  };

  const beginStream = (): void => {
    if (cancelled || connection?.state !== HubConnectionState.Connected) {
      return;
    }

    streamSubscription?.dispose();
    let receivedSnapshot = false;
    streamSubscription = connection.stream<ResourcesEvent>("WatchResources").subscribe({
      next: (event) => {
        if (!isResourcesEvent(event)) {
          stopForStreamError(new Error("Dashboard resource stream returned an incompatible event."));
          return;
        }
        if (!receivedSnapshot && event.type !== "snapshot") {
          stopForStreamError(new Error("Dashboard resource stream sent a change before its initial snapshot."));
          return;
        }

        receivedSnapshot = true;
        callback(event);
        connectionFailed = false;
        reportConnection({ target: "resourceService", state: "connected" });
      },
      error: (error) => {
        streamSubscription = null;
        if (connection?.state === HubConnectionState.Connected) {
          stopForStreamError(error);
        }
      },
      complete: () => {
        streamSubscription = null;
        if (connection?.state === HubConnectionState.Connected) {
          stopForStreamError(new Error("Dashboard resource stream ended unexpectedly."));
        }
      },
    });
  };

  const scheduleStart = (): void => {
    if (cancelled || retryTimer !== undefined) {
      return;
    }

    retryTimer = window.setTimeout(() => {
      retryTimer = undefined;
      void start();
    }, 1_000);
  };

  const start = async (): Promise<void> => {
    if (cancelled || starting || (connection !== null && connection.state !== HubConnectionState.Disconnected)) {
      return;
    }

    starting = true;
    // Once an attempt has failed, keep the stable error status visible while automatic
    // retries run in the background. Alternating error/connecting every second makes the
    // status pill pulse and looks like the disconnected dashboard is being remounted.
    if (!connectionFailed) {
      reportConnection({ target: "resourceService", state: "connecting" });
    }
    try {
      if (connection === null) {
        const version = await getNegotiatedVersion();
        if (!version.capabilities.includes(resourceStreamCapability)) {
          throw new Error("Dashboard API version 1 does not advertise the live resources capability.");
        }

        connection = new HubConnectionBuilder()
          .withUrl(`${version.basePath}/resources/live`, { withCredentials: true })
          .withAutomaticReconnect([0, 1_000, 2_000, 5_000])
          .configureLogging(LogLevel.None)
          .build();
        connection.onreconnecting((error) => {
          streamSubscription = null;
          reportConnection({
            target: "resourceService",
            state: "connecting",
            message: error?.message,
          });
        });
        connection.onreconnected(() => beginStream());
        connection.onclose((error) => {
          streamSubscription = null;
          if (!cancelled) {
            if (error !== undefined) {
              reportError(error);
            }
            scheduleStart();
          }
        });
      }

      await connection.start();
      beginStream();
    } catch (error) {
      if (!cancelled) {
        reportError(error);
        scheduleStart();
      }
    } finally {
      starting = false;
    }
  };

  const retry = (): void => {
    if (retryTimer !== undefined) {
      window.clearTimeout(retryTimer);
      retryTimer = undefined;
    }
    void start();
  };

  registerRetry(retry);
  void start();
  return () => {
    cancelled = true;
    registerRetry(null);
    if (retryTimer !== undefined) {
      window.clearTimeout(retryTimer);
    }
    streamSubscription?.dispose();
    void connection?.stop();
  };
}

function subscribeStructuredLogs(callback: (logs: NativeStructuredLogs) => void): () => void {
  let cancelled = false;
  let connection: HubConnection | null = null;
  let subscription: ISubscription<DashboardStructuredLogsEvent> | null = null;
  let retryTimer: number | undefined;

  const start = async (): Promise<void> => {
    try {
      const version = await getNegotiatedVersion();
      if (!version.capabilities.includes(structuredLogsCapability)
          || !version.capabilities.includes(structuredLogStreamCapability)) {
        throw new Error("Dashboard API version 1 does not advertise live structured logs.");
      }

      await refreshStructuredLogs();
      if (cancelled) return;

      connection = new HubConnectionBuilder()
        .withUrl(`${version.basePath}/structured-logs/live`, { withCredentials: true })
        .withAutomaticReconnect([0, 1_000, 2_000, 5_000])
        .configureLogging(LogLevel.None)
        .build();
      connection.onreconnected(() => {
        void refreshStructuredLogs().catch(() => undefined);
        beginStream();
      });
      connection.onclose(() => {
        subscription = null;
        if (!cancelled) retryTimer = window.setTimeout(() => void start(), 1_000);
      });
      await connection.start();
      beginStream();
    } catch {
      if (!cancelled) retryTimer = window.setTimeout(() => void start(), 1_000);
    }
  };

  const beginStream = (): void => {
    if (cancelled || connection?.state !== HubConnectionState.Connected) return;
    subscription?.dispose();
    subscription = connection.stream<DashboardStructuredLogsEvent>("WatchStructuredLogs").subscribe({
      next: (event) => {
        if (typeof event !== "object" || event === null || typeof event.data !== "object" || event.data === null) {
          void connection?.stop();
          return;
        }
        appendStructuredLogEvent(event);
      },
      error: () => {
        subscription = null;
        if (connection?.state === HubConnectionState.Connected) void connection.stop();
      },
      complete: () => {
        subscription = null;
        if (connection?.state === HubConnectionState.Connected) void connection.stop();
      },
    });
  };

  structuredLogListeners.add(callback);
  callback(structuredLogs);
  void start();
  return () => {
    cancelled = true;
    structuredLogListeners.delete(callback);
    if (retryTimer !== undefined) window.clearTimeout(retryTimer);
    subscription?.dispose();
    void connection?.stop();
  };
}

function subscribeConsoleLogs(
  resourceName: string,
  callback: (event: ConsoleLogEvent) => void,
): () => void {
  let cancelled = false;
  let connection: HubConnection | null = null;
  let subscription: ISubscription<ConsoleLogEvent> | null = null;
  let retryTimer: number | undefined;
  let highestLineNumber = 0;

  const beginStream = (): void => {
    if (cancelled || connection?.state !== HubConnectionState.Connected) return;
    subscription?.dispose();
    subscription = connection.stream<ConsoleLogEvent>("WatchConsoleLogs", resourceName).subscribe({
      next: (event) => {
        if (cancelled || event.resourceName !== resourceName || !Array.isArray(event.lines)) {
          return;
        }

        // A reconnect replays the resource backlog before returning to live data. Line
        // numbers are monotonic per resource, so discard the replayed prefix while keeping
        // the legacy endpoint's backlog-to-live order and stdout/stderr identity intact.
        const lines = event.lines.filter((line) =>
          Number.isInteger(line.lineNumber)
          && line.lineNumber > highestLineNumber
          && typeof line.text === "string"
          && typeof line.isStdErr === "boolean");
        if (lines.length === 0) return;
        highestLineNumber = Math.max(highestLineNumber, ...lines.map((line) => line.lineNumber));
        callback({ resourceName, lines });
      },
      error: () => {
        subscription = null;
        if (connection?.state === HubConnectionState.Connected) void connection.stop();
      },
      complete: () => {
        subscription = null;
        if (connection?.state === HubConnectionState.Connected) void connection.stop();
      },
    });
  };

  const scheduleStart = (): void => {
    if (cancelled || retryTimer !== undefined) return;
    retryTimer = window.setTimeout(() => {
      retryTimer = undefined;
      void start();
    }, 1_000);
  };

  const start = async (): Promise<void> => {
    try {
      const version = await getNegotiatedVersion();
      if (!version.capabilities.includes(consoleLogsCapability)
          || !version.capabilities.includes(consoleLogStreamCapability)) {
        throw new Error("Dashboard API version 1 does not advertise live console logs.");
      }
      if (cancelled) return;

      connection = new HubConnectionBuilder()
        .withUrl(`${version.basePath}/console-logs/live`, { withCredentials: true })
        .withAutomaticReconnect([0, 1_000, 2_000, 5_000])
        .configureLogging(LogLevel.None)
        .build();
      connection.onreconnected(beginStream);
      connection.onclose(scheduleStart);
      await connection.start();
      beginStream();
    } catch {
      scheduleStart();
    }
  };

  void start();
  return () => {
    cancelled = true;
    if (retryTimer !== undefined) window.clearTimeout(retryTimer);
    subscription?.dispose();
    void connection?.stop();
  };
}

export const nativeBackend = {
  getConfig,
  hasCapability,
  listResources,
  executeCommand,
  subscribeResources,
  subscribeStructuredLogs,
  subscribeConsoleLogs,
  refreshStructuredLogs,
};
