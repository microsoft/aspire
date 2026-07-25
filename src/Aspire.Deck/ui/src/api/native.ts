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
  DashboardTraceEvent,
  DashboardTraceSnapshot,
  DeckConfig,
  CommandResponse,
  ExecuteCommandArgs,
  InteractionInfo,
  MetricSeriesQuery,
  MetricSeriesResponse,
  MetricSummary,
  Resource,
  ResourcesEvent,
  LogRecordSummary,
  SpanSummary,
} from "./types";
import {
  getLogRecordSummaries,
  getSpanSummaries,
  type OtlpLogRecordSummary,
  type OtlpSpanSummary,
} from "./otlp";

const dashboardProduct = "Aspire.Dashboard";
const discoveryPath = "/api/dashboard";
const configurationCapability = "configuration";
const shellCapability = "shell";
const cultureCapability = "culture";
const authenticationCapability = "authentication";
const resourcesCapability = "resources";
const resourceStreamCapability = "resources-live";
const commandsCapability = "commands";
const structuredLogsCapability = "structured-logs";
const structuredLogStreamCapability = "structured-logs-live";
const structuredLogClearCapability = "structured-logs-clear";
const tracesCapability = "traces";
const traceStreamCapability = "traces-live";
const traceClearCapability = "traces-clear";
const metricsCapability = "metrics";
const metricSeriesCapability = "metrics-series";
const metricClearCapability = "metrics-clear";
const consoleLogsCapability = "console-logs";
const consoleLogStreamCapability = "console-logs-live";
const terminalCapability = "terminal";
const interactionsCapability = "interactions";
const maximumStructuredLogDedupeKeys = 10_000;
const maximumTraceDedupeKeys = 10_000;
const supportedVersions = new Set([1]);

let negotiatedVersion: Promise<DashboardApiVersion> | null = null;
let configuration: Promise<DeckConfig> | null = null;
let authenticationRedirectStarted = false;
const structuredLogListeners = new Set<(logs: NativeStructuredLogs) => void>();
const structuredLogKeys = new Map<string, true>();
const structuredLogRestartListeners = new Set<() => void>();
let structuredLogs: NativeStructuredLogs = { logCount: 0, recentLogs: [] };
let structuredLogGeneration = 0;
const traceListeners = new Set<(traces: NativeTraces) => void>();
const traceKeys = new Map<string, true>();
const traceRestartListeners = new Set<() => void>();
let traces: NativeTraces = { spanCount: 0, recentSpans: [] };
let traceGeneration = 0;
const metricListeners = new Set<(metrics: NativeMetrics) => void>();
let metricSummary: NativeMetrics = { metricCount: 0, metrics: [] };
let metricRefreshPromise: Promise<void> | null = null;
let metricPollTimer: number | undefined;

interface NativeStructuredLogs {
  logCount: number;
  recentLogs: LogRecordSummary[];
}

interface NativeTraces {
  spanCount: number;
  recentSpans: SpanSummary[];
}

interface NativeMetrics {
  metricCount: number;
  metrics: MetricSummary[];
}

function isMetricSummary(value: unknown): value is MetricSummary {
  if (typeof value !== "object" || value === null) return false;
  const summary = value as Partial<MetricSummary>;
  return typeof summary.name === "string"
    && (typeof summary.description === "string" || summary.description == null)
    && (typeof summary.meterName === "string" || summary.meterName == null)
    && (typeof summary.unit === "string" || summary.unit === null)
    && (typeof summary.resourceName === "string" || summary.resourceName === null)
    && (summary.kind === "gauge"
      || summary.kind === "counter"
      || summary.kind === "upDownCounter"
      || summary.kind === "histogram")
    && (typeof summary.lastValue === "number" || summary.lastValue === null)
    && typeof summary.pointCount === "number"
    && Number.isFinite(summary.pointCount)
    && summary.pointCount >= 0;
}

function isNumberArray(value: unknown): value is number[] {
  return Array.isArray(value) && value.every((item) => typeof item === "number" && Number.isFinite(item));
}

function isMetricSeriesResponse(value: unknown): value is MetricSeriesResponse {
  if (typeof value !== "object" || value === null) return false;
  const series = value as Partial<MetricSeriesResponse>;
  return typeof series.name === "string"
    && (typeof series.meterName === "string" || series.meterName == null)
    && (typeof series.resourceName === "string" || series.resourceName === null)
    && (typeof series.unit === "string" || series.unit === null)
    && (series.kind === "gauge"
      || series.kind === "counter"
      || series.kind === "upDownCounter"
      || series.kind === "histogram")
    && isNumberArray(series.timestampsMs)
    // The versioned payload deliberately mirrors the dashboard metric DTOs. Arrays
    // that do not apply to the selected instrument or histogram mode are serialized
    // as null rather than omitted, so both shapes are valid on the wire.
    && (series.values == null || isNumberArray(series.values))
    && (series.p50 == null || isNumberArray(series.p50))
    && (series.p90 == null || isNumberArray(series.p90))
    && (series.p99 == null || isNumberArray(series.p99))
    && (series.sum == null || isNumberArray(series.sum))
    && (series.bucketBounds == null || isNumberArray(series.bucketBounds));
}

function compareNewestFirst(left: LogRecordSummary, right: LogRecordSummary): number {
  if (left.timeUnixNano.length !== right.timeUnixNano.length) {
    return right.timeUnixNano.length - left.timeUnixNano.length;
  }
  return right.timeUnixNano.localeCompare(left.timeUnixNano);
}

function compareSpansNewestFirst(left: SpanSummary, right: SpanSummary): number {
  if (left.startUnixNano.length !== right.startUnixNano.length) {
    return right.startUnixNano.length - left.startUnixNano.length;
  }
  return right.startUnixNano.localeCompare(left.startUnixNano);
}

function withoutRecordKey(log: OtlpLogRecordSummary): LogRecordSummary {
  const { recordKey: _, ...summary } = log;
  return summary;
}

function notifyStructuredLogs(): void {
  for (const listener of structuredLogListeners) listener(structuredLogs);
}

function rememberStructuredLogKey(key: string): boolean {
  if (structuredLogKeys.has(key)) return false;

  structuredLogKeys.set(key, true);
  if (structuredLogKeys.size > maximumStructuredLogDedupeKeys) {
    const oldestKey = structuredLogKeys.keys().next().value;
    if (oldestKey !== undefined) structuredLogKeys.delete(oldestKey);
  }

  return true;
}

function notifyTraces(): void {
  for (const listener of traceListeners) listener(traces);
}

async function refreshStructuredLogs(): Promise<void> {
  const version = await getNegotiatedVersion();
  const snapshot = await requestJson(`${version.basePath}/structured-logs`) as DashboardStructuredLogsSnapshot;
  if (!Number.isInteger(snapshot.totalCount) || typeof snapshot.data !== "object" || snapshot.data === null) {
    throw new Error("Dashboard API structured-log snapshot returned an incompatible payload.");
  }
  const records = getLogRecordSummaries(snapshot.data);
  structuredLogKeys.clear();
  for (const record of records) rememberStructuredLogKey(record.recordKey);
  structuredLogs = {
    logCount: snapshot.totalCount,
    recentLogs: records.map(withoutRecordKey).sort(compareNewestFirst),
  };
  notifyStructuredLogs();
}

async function getStructuredLogs(): Promise<NativeStructuredLogs> {
  await refreshStructuredLogs();
  return structuredLogs;
}

function appendStructuredLogEvent(event: DashboardStructuredLogsEvent, generation: number): void {
  if (generation !== structuredLogGeneration) return;
  const additions = getLogRecordSummaries(event.data).filter((log) => {
    return rememberStructuredLogKey(log.recordKey);
  }).map(withoutRecordKey);
  if (additions.length === 0) return;
  structuredLogs = {
    logCount: structuredLogs.logCount + additions.length,
    recentLogs: [...additions, ...structuredLogs.recentLogs].sort(compareNewestFirst).slice(0, 5_000),
  };
  notifyStructuredLogs();
}

function withoutSpanRecordKey(span: OtlpSpanSummary): SpanSummary {
  const { recordKey: _, ...summary } = span;
  return summary;
}

function rememberTraceKey(key: string): boolean {
  if (traceKeys.has(key)) return false;

  traceKeys.set(key, true);
  if (traceKeys.size > maximumTraceDedupeKeys) {
    const oldestKey = traceKeys.keys().next().value;
    if (oldestKey !== undefined) traceKeys.delete(oldestKey);
  }

  return true;
}

async function refreshTraces(): Promise<void> {
  const version = await getNegotiatedVersion();
  const snapshot = await requestJson(`${version.basePath}/traces?limit=10000`) as DashboardTraceSnapshot;
  if (!Number.isInteger(snapshot.totalCount)
      || !Number.isInteger(snapshot.returnedCount)
      || typeof snapshot.data !== "object"
      || snapshot.data === null) {
    throw new Error("Dashboard API trace snapshot returned an incompatible payload.");
  }
  const records = getSpanSummaries(snapshot.data);
  traceKeys.clear();
  for (const record of records) rememberTraceKey(record.recordKey);
  traces = {
    spanCount: snapshot.totalCount,
    recentSpans: records
      .map(withoutSpanRecordKey)
      .sort(compareSpansNewestFirst)
      .slice(0, 5_000),
  };
  notifyTraces();
}

async function getTraces(): Promise<NativeTraces> {
  await refreshTraces();
  return traces;
}

function notifyMetrics(): void {
  for (const listener of metricListeners) listener(metricSummary);
}

function refreshMetrics(): Promise<void> {
  if (metricRefreshPromise !== null) return metricRefreshPromise;

  const refresh = getNegotiatedVersion()
    .then((version) => requestJson(`${version.basePath}/metrics`))
    .then((value) => {
      if (!Array.isArray(value) || !value.every(isMetricSummary)) {
        throw new Error("Dashboard API metric summaries returned an incompatible payload.");
      }
      const summaries = value;
      metricSummary = {
        metricCount: summaries.reduce((total, metric) => total + metric.pointCount, 0),
        metrics: summaries,
      };
      notifyMetrics();
    });
  metricRefreshPromise = refresh;
  const clearRefresh = (): void => {
    if (metricRefreshPromise === refresh) metricRefreshPromise = null;
  };
  void refresh.then(clearRefresh, clearRefresh);
  return refresh;
}

async function getMetrics(): Promise<NativeMetrics> {
  await refreshMetrics();
  return metricSummary;
}

function appendTraceEvent(event: DashboardTraceEvent, generation: number): void {
  if (generation !== traceGeneration) return;
  const additions = getSpanSummaries(event.data)
    .filter((span) => rememberTraceKey(span.recordKey))
    .map(withoutSpanRecordKey);
  if (additions.length === 0) return;
  traces = {
    spanCount: traces.spanCount + additions.length,
    recentSpans: [...additions, ...traces.recentSpans]
      .sort(compareSpansNewestFirst)
      .slice(0, 5_000),
  };
  notifyTraces();
}

async function requestJson(path: string): Promise<unknown> {
  const response = await fetch(path, {
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/json" },
  });
  transferAuthenticationRedirect(response);
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
  transferAuthenticationRedirect(response);
  if (!response.ok) {
    throw new Error(`Dashboard API request failed with ${response.status} ${response.statusText}.`);
  }

  return await response.json() as unknown;
}

async function deleteNoContent(path: string): Promise<void> {
  const response = await fetch(path, {
    method: "DELETE",
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/json" },
  });
  transferAuthenticationRedirect(response);
  if (!response.ok) {
    throw new Error(`Dashboard API request failed with ${response.status} ${response.statusText}.`);
  }

  await response.arrayBuffer();
}

function transferAuthenticationRedirect(response: Response): void {
  if (!response.redirected) return;

  const redirectUrl = new URL(response.url);
  if (redirectUrl.origin === window.location.origin
      && (redirectUrl.pathname === "/login" || redirectUrl.pathname.startsWith("/authentication/"))) {
    if (!authenticationRedirectStarted) {
      authenticationRedirectStarted = true;
      window.location.assign(`${redirectUrl.pathname}${redirectUrl.search}${redirectUrl.hash}`);
    }
    throw new Error("Dashboard authentication is required.");
  }
}

function isDeckConfig(value: unknown): value is DeckConfig {
  if (typeof value !== "object" || value === null) return false;
  const config = value as Partial<DeckConfig>;
  return (typeof config.applicationName === "string" || config.applicationName === null)
    && (typeof config.resourceServiceUrl === "string" || config.resourceServiceUrl === null)
    && (typeof config.otlpGrpcUrl === "string" || config.otlpGrpcUrl === null)
    && (typeof config.otlpHttpUrl === "string" || config.otlpHttpUrl === null)
    && typeof config.version === "string"
    && (config.runtimeVersion === undefined || typeof config.runtimeVersion === "string")
    && (config.isTelemetryEndpointUnsecured === undefined || typeof config.isTelemetryEndpointUnsecured === "boolean")
    && (config.isApiEndpointUnsecured === undefined || typeof config.isApiEndpointUnsecured === "boolean")
    && (config.frontendAuthMode === undefined || typeof config.frontendAuthMode === "string")
    && (config.user === undefined
      || config.user === null
      || (typeof config.user === "object"
        && config.user !== null
        && typeof config.user.name === "string"
        && (typeof config.user.username === "string" || config.user.username === null)))
    && (config.culture === undefined || typeof config.culture === "string")
    && (config.cultures === undefined
      || (Array.isArray(config.cultures)
        && config.cultures.every((culture) =>
          typeof culture === "object"
          && culture !== null
          && typeof culture.name === "string"
          && typeof culture.displayName === "string")))
    && (config.isAgentHelpEnabled === undefined || typeof config.isAgentHelpEnabled === "boolean")
    && (config.agentHelpMarkdown === undefined
      || config.agentHelpMarkdown === null
      || typeof config.agentHelpMarkdown === "string")
    && (config.isAssistantEnabled === undefined || typeof config.isAssistantEnabled === "boolean");
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
  if (version.capabilities.includes(shellCapability)) {
    const shell = await requestJson(`${version.basePath}/shell`);
    if (!isDeckConfig(shell)) {
      throw new Error("Dashboard API shell configuration returned an incompatible payload.");
    }
    return shell;
  }

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

async function getCultureUrl(language: string, redirectUrl: string): Promise<string | null> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(cultureCapability)) {
    return null;
  }

  const query = new URLSearchParams({ language, redirectUrl });
  return `${version.basePath}/culture?${query}`;
}

async function getSignOutPath(): Promise<string | null> {
  const version = await getNegotiatedVersion();
  return version.capabilities.includes(authenticationCapability)
    ? `${version.basePath}/authentication/logout`
    : null;
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

async function getTerminalWebSocketUrl(resourceName: string, replicaIndex: number): Promise<string | null> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(terminalCapability)) {
    return null;
  }

  const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  const query = new URLSearchParams({
    resource: resourceName,
    replica: replicaIndex.toString(),
  });
  return `${protocol}//${window.location.host}${version.basePath}/terminal?${query}`;
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

function subscribeInteractions(callback: (interactions: InteractionInfo[]) => void): () => void {
  let cancelled = false;
  let timer: number | undefined;

  const poll = async (): Promise<void> => {
    try {
      const version = await getNegotiatedVersion();
      if (!version.capabilities.includes(interactionsCapability)) {
        throw new Error("Dashboard API version 1 does not advertise interactions.");
      }
      const payload = await requestJson(`${version.basePath}/interactions`);
      if (!Array.isArray(payload)) {
        throw new Error("Dashboard API interactions returned an incompatible payload.");
      }
      if (!cancelled) callback(payload as InteractionInfo[]);
    } catch {
      // Resource streaming owns the connection indicator. Retain the last interaction
      // snapshot through a transient failure so an open input dialog is not discarded.
    } finally {
      if (!cancelled) timer = window.setTimeout(() => void poll(), 250);
    }
  };

  void poll();
  return () => {
    cancelled = true;
    if (timer !== undefined) window.clearTimeout(timer);
  };
}

async function respondInteraction(
  interactionId: number,
  action: string,
  values: Record<string, string>,
): Promise<void> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(interactionsCapability)) {
    throw new Error("Dashboard API version 1 does not advertise interactions.");
  }

  const response = await fetch(`${version.basePath}/interactions/respond`, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ interactionId, action, values }),
  });
  if (!response.ok) {
    throw new Error(`Dashboard API request failed with ${response.status} ${response.statusText}.`);
  }

  // Consume even an empty 204 response so Chromium finishes the request before a
  // command result closes the interaction or a test navigates away.
  await response.arrayBuffer();
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
  let starting = false;
  let connection: HubConnection | null = null;
  let subscription: ISubscription<DashboardStructuredLogsEvent> | null = null;
  let retryTimer: number | undefined;

  const scheduleStart = (): void => {
    if (cancelled || retryTimer !== undefined) return;
    retryTimer = window.setTimeout(() => {
      retryTimer = undefined;
      void start();
    }, 1_000);
  };

  const beginStream = (): void => {
    if (cancelled || connection?.state !== HubConnectionState.Connected) return;
    subscription?.dispose();
    const generation = structuredLogGeneration;
    subscription = connection.stream<DashboardStructuredLogsEvent>("WatchStructuredLogs").subscribe({
      next: (event) => {
        if (typeof event !== "object" || event === null || typeof event.data !== "object" || event.data === null) {
          void connection?.stop();
          return;
        }
        appendStructuredLogEvent(event, generation);
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

  const start = async (): Promise<void> => {
    if (cancelled || starting) return;
    starting = true;
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
        void refreshStructuredLogs().then(beginStream).catch(() => void connection?.stop());
      });
      connection.onclose(() => {
        subscription = null;
        connection = null;
        scheduleStart();
      });
      await connection.start();
      beginStream();
    } catch {
      connection = null;
      scheduleStart();
    } finally {
      starting = false;
    }
  };

  const restart = (): void => {
    if (cancelled) return;
    subscription?.dispose();
    subscription = null;
    const previousConnection = connection;
    connection = null;
    const stopped = previousConnection?.stop() ?? Promise.resolve();
    void stopped.finally(() => {
      if (!cancelled) void start();
    });
  };

  structuredLogListeners.add(callback);
  structuredLogRestartListeners.add(restart);
  callback(structuredLogs);
  void start();
  return () => {
    cancelled = true;
    structuredLogListeners.delete(callback);
    structuredLogRestartListeners.delete(restart);
    if (retryTimer !== undefined) window.clearTimeout(retryTimer);
    subscription?.dispose();
    void connection?.stop();
  };
}

function subscribeTraces(callback: (traces: NativeTraces) => void): () => void {
  let cancelled = false;
  let starting = false;
  let connection: HubConnection | null = null;
  let subscription: ISubscription<DashboardTraceEvent> | null = null;
  let retryTimer: number | undefined;

  const scheduleStart = (): void => {
    if (cancelled || retryTimer !== undefined) return;
    retryTimer = window.setTimeout(() => {
      retryTimer = undefined;
      void start();
    }, 1_000);
  };

  const beginStream = (): void => {
    if (cancelled || connection?.state !== HubConnectionState.Connected) return;
    subscription?.dispose();
    const generation = traceGeneration;
    subscription = connection.stream<DashboardTraceEvent>("WatchTraces", {
      resourceNames: [],
      traceId: null,
      hasError: null,
      search: null,
    }).subscribe({
      next: (event) => {
        if (typeof event !== "object" || event === null || typeof event.data !== "object" || event.data === null) {
          void connection?.stop();
          return;
        }
        appendTraceEvent(event, generation);
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

  const start = async (): Promise<void> => {
    if (cancelled || starting) return;
    starting = true;
    try {
      const version = await getNegotiatedVersion();
      if (!version.capabilities.includes(tracesCapability)
          || !version.capabilities.includes(traceStreamCapability)) {
        throw new Error("Dashboard API version 1 does not advertise live traces.");
      }

      await refreshTraces();
      if (cancelled) return;

      connection = new HubConnectionBuilder()
        .withUrl(`${version.basePath}/traces/live`, { withCredentials: true })
        .withAutomaticReconnect([0, 1_000, 2_000, 5_000])
        .configureLogging(LogLevel.None)
        .build();
      connection.onreconnected(() => {
        void refreshTraces().then(beginStream).catch(() => void connection?.stop());
      });
      connection.onclose(() => {
        subscription = null;
        connection = null;
        scheduleStart();
      });
      await connection.start();
      beginStream();
    } catch {
      connection = null;
      scheduleStart();
    } finally {
      starting = false;
    }
  };

  const restart = (): void => {
    if (cancelled) return;
    subscription?.dispose();
    subscription = null;
    const previousConnection = connection;
    connection = null;
    const stopped = previousConnection?.stop() ?? Promise.resolve();
    void stopped.finally(() => {
      if (!cancelled) void start();
    });
  };

  traceListeners.add(callback);
  traceRestartListeners.add(restart);
  callback(traces);
  void start();
  return () => {
    cancelled = true;
    traceListeners.delete(callback);
    traceRestartListeners.delete(restart);
    if (retryTimer !== undefined) window.clearTimeout(retryTimer);
    subscription?.dispose();
    void connection?.stop();
  };
}

function subscribeMetrics(callback: (metrics: NativeMetrics) => void): () => void {
  metricListeners.add(callback);
  callback(metricSummary);
  void refreshMetrics().catch(() => undefined);
  if (metricPollTimer === undefined) {
    metricPollTimer = window.setInterval(() => void refreshMetrics().catch(() => undefined), 1_500);
  }

  return () => {
    metricListeners.delete(callback);
    if (metricListeners.size === 0 && metricPollTimer !== undefined) {
      window.clearInterval(metricPollTimer);
      metricPollTimer = undefined;
    }
  };
}

async function clearStructuredLogs(resourceName: string | null): Promise<void> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(structuredLogsCapability)
      || !version.capabilities.includes(structuredLogStreamCapability)
      || !version.capabilities.includes(structuredLogClearCapability)) {
    throw new Error("Dashboard API version 1 does not advertise structured-log clearing.");
  }

  const resourceQuery = resourceName === null ? "" : `?resource=${encodeURIComponent(resourceName)}`;
  await deleteNoContent(`${version.basePath}/structured-logs${resourceQuery}`);

  // A pre-clear stream can still have buffered additions. Ignore that generation, replace
  // local identity state from the post-clear snapshot, then reconnect for a clean live handoff.
  structuredLogGeneration++;
  try {
    await refreshStructuredLogs();
  } finally {
    for (const restart of structuredLogRestartListeners) restart();
  }
}

async function clearTraces(resourceName: string | null): Promise<void> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(tracesCapability)
      || !version.capabilities.includes(traceStreamCapability)
      || !version.capabilities.includes(traceClearCapability)) {
    throw new Error("Dashboard API version 1 does not advertise trace clearing.");
  }

  const resourceQuery = resourceName === null ? "" : `?resource=${encodeURIComponent(resourceName)}`;
  await deleteNoContent(`${version.basePath}/traces${resourceQuery}`);

  // Ignore any events already buffered by the pre-clear stream. Restarting opens a new
  // upstream watcher whose backlog reflects the authoritative post-clear repository.
  traceGeneration++;
  try {
    await refreshTraces();
  } finally {
    for (const restart of traceRestartListeners) restart();
  }
}

async function clearMetrics(resourceName: string | null): Promise<void> {
  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(metricsCapability)
      || !version.capabilities.includes(metricSeriesCapability)
      || !version.capabilities.includes(metricClearCapability)) {
    throw new Error("Dashboard API version 1 does not advertise metric clearing.");
  }

  const resourceQuery = resourceName === null ? "" : `?resource=${encodeURIComponent(resourceName)}`;
  await deleteNoContent(`${version.basePath}/metrics${resourceQuery}`);
  const preClearRefresh = metricRefreshPromise;
  if (preClearRefresh !== null) {
    await preClearRefresh.catch(() => undefined);
  }
  await refreshMetrics();
}

async function getMetricSeries(query: MetricSeriesQuery): Promise<MetricSeriesResponse | null> {
  if (!query.resourceName || !query.meterName) {
    return null;
  }

  const version = await getNegotiatedVersion();
  if (!version.capabilities.includes(metricsCapability)
      || !version.capabilities.includes(metricSeriesCapability)) {
    throw new Error("Dashboard API version 1 does not advertise metric series.");
  }

  const search = new URLSearchParams({
    resource: query.resourceName,
    meter: query.meterName,
    instrument: query.name,
    windowSeconds: String(query.windowSeconds ?? 300),
    maxPoints: String(query.maxPoints ?? 400),
    showCount: String(query.showCount ?? false),
    histogramMode: query.histogramMode ?? (query.showCount ? "count" : "percentiles"),
  });
  for (const [name, values] of Object.entries(query.dimensions ?? {})) {
    if (values.length === 0) {
      search.append(`dimension.${name}`, "x:");
    }
    for (const value of values) {
      search.append(`dimension.${name}`, value === null ? "n:" : `s:${value}`);
    }
  }

  const response = await fetch(`${version.basePath}/metrics/series?${search}`, {
    cache: "no-store",
    credentials: "same-origin",
    headers: { Accept: "application/json" },
  });
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    throw new Error(`Dashboard API request failed with ${response.status} ${response.statusText}.`);
  }
  const series = await response.json() as unknown;
  if (!isMetricSeriesResponse(series)) {
    throw new Error("Dashboard API metric series returned an incompatible payload.");
  }
  return series;
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
  getCultureUrl,
  getSignOutPath,
  getTerminalWebSocketUrl,
  listResources,
  executeCommand,
  subscribeResources,
  subscribeStructuredLogs,
  subscribeTraces,
  subscribeMetrics,
  subscribeConsoleLogs,
  subscribeInteractions,
  respondInteraction,
  getStructuredLogs,
  refreshStructuredLogs,
  clearStructuredLogs,
  getTraces,
  refreshTraces,
  clearTraces,
  getMetrics,
  refreshMetrics,
  clearMetrics,
  getMetricSeries,
};
