// Rich, self-contained mock backend used when the UI runs outside Tauri
// (browser dev / `npm run preview`). It mirrors the command + event surface
// defined in CONTRACT.md so that App code is identical in both modes.

import { PARAMETER_VALUE_PROPERTY } from "./types";
import type {
  AppHostInfo,
  AssistantChatRequest,
  AssistantEvent,
  AssistantInfo,
  CanvasManifest,
  CommandResponse,
  ConnectionStatus,
  ConsoleLogEvent,
  DeckConfig,
  ExecuteCommandArgs,
  InteractionInfo,
  InteractionInputInfo,
  LogRecordSummary,
  MetricKind,
  MetricSummary,
  MetricSeriesResponse,
  MetricSeriesQuery,
  ManageDataExport,
  ManageDataRequest,
  ManageDataResponse,
  Resource,
  ResourceCommand,
  ResourcesEvent,
  SpanSummary,
  TelemetrySummary,
} from "./types";

type Unsubscribe = () => void;

function toUnixNano(timestampMs: number): string {
  // OTLP uses unix nanoseconds; keep as string to avoid bigint loss.
  return (BigInt(timestampMs) * 1_000_000n).toString();
}

function isoMinutesAgo(minutes: number): string {
  return new Date(Date.now() - minutes * 60_000).toISOString();
}

function indexedHex(value: number, length: number): string {
  const segment = value.toString(16).padStart(8, "0");
  return segment.repeat(Math.ceil(length / segment.length)).slice(0, length);
}

const config: DeckConfig = {
  applicationName: "TestShop",
  resourceServiceUrl: "https://localhost:17042",
  otlpGrpcUrl: "https://localhost:18889",
  otlpHttpUrl: "https://localhost:18890",
  version: "9.0.0-dev (mock)",
  runtimeVersion: "Browser mock runtime",
  frontendAuthMode: "Unsecured",
  user: null,
  culture: "en",
  cultures: [
    { name: "en", displayName: "English" },
    { name: "fr", displayName: "Français" },
  ],
  isAgentHelpEnabled: true,
  agentHelpMarkdown: "Give AI agents deep observability into your app so they can diagnose issues faster and verify fixes with confidence.\n\n- Resource state, health checks, and relationships\n- Console logs\n- Distributed traces\n- Structured logs\n\nInitialize AI agent support in your project with:\n\n```bash\naspire agent init\n```\n\nFor more information, see [AI coding agents](https://aka.ms/aspire/ai-agents-apphost).",
  isAssistantEnabled: true,
};

function makeResources(): Resource[] {
  return [
    {
      name: "frontend",
      resourceType: "Project",
      displayName: "frontend",
      uid: "uid-frontend",
      state: "Running",
      stateStyle: "success",
      health: "Healthy",
      createdAt: isoMinutesAgo(12),
      startedAt: isoMinutesAgo(12),
      stoppedAt: null,
      urls: [
        { name: "https", url: "https://localhost:7233", isInternal: false, isInactive: false, displayName: "https", sortOrder: 0 },
        { name: "http", url: "http://localhost:5233", isInternal: false, isInactive: false, displayName: "http", sortOrder: 1 },
      ],
      properties: [
        { name: "project.path", displayName: "Project path", value: "src/TestShop/Frontend/Frontend.csproj", isSensitive: false, isHighlighted: true, sortOrder: 0 },
        { name: "executable.pid", displayName: "PID", value: "48213", isSensitive: false, isHighlighted: false, sortOrder: 1 },
        { name: "executable.path", displayName: "Executable", value: "/usr/local/share/dotnet/dotnet", isSensitive: false, isHighlighted: false, sortOrder: 2 },
        { name: "custom.object", displayName: "Deployment metadata", value: '{"region":"west","replicas":2}', isSensitive: false, isHighlighted: false, sortOrder: 3 },
        { name: "custom.array", displayName: "Feature flags", value: '["catalog","checkout"]', isSensitive: false, isHighlighted: false, sortOrder: 4 },
        { name: "custom.null", displayName: "Optional owner", value: "null", isSensitive: false, isHighlighted: false, sortOrder: 5 },
      ],
      environment: [
        { name: "ASPNETCORE_ENVIRONMENT", value: "Development", isFromSpec: true },
        { name: "ASPNETCORE_URLS", value: "https://localhost:7233;http://localhost:5233", isFromSpec: true },
        { name: "services__apiservice__https__0", value: "https://localhost:7355", isFromSpec: true },
        { name: "ConnectionStrings__cache", value: "localhost:6379,password=p@ssw0rd-redis", isFromSpec: false },
      ],
      healthReports: [
        { status: "Healthy", key: "self", description: "Liveness probe succeeded." },
        { status: "Healthy", key: "apiservice", description: "Upstream apiservice reachable." },
      ],
      commands: defaultCommands("Running"),
      relationships: [{ resourceName: "apiservice", type: "Reference" }, { resourceName: "cache", type: "Reference" }],
      isHidden: false,
      supportsDetailedTelemetry: true,
      iconName: "Window",
      iconVariant: "regular",
    },
    {
      name: "apiservice",
      resourceType: "Project",
      displayName: "apiservice",
      uid: "uid-apiservice",
      state: "Running",
      stateStyle: "success",
      health: "Healthy",
      createdAt: isoMinutesAgo(12),
      startedAt: isoMinutesAgo(12),
      stoppedAt: null,
      urls: [
        { name: "https", url: "https://localhost:7355", isInternal: false, isInactive: false, displayName: "https", sortOrder: 0 },
      ],
      properties: [
        { name: "project.path", displayName: "Project path", value: "src/TestShop/ApiService/ApiService.csproj", isSensitive: false, isHighlighted: true, sortOrder: 0 },
        { name: "executable.pid", displayName: "PID", value: "48215", isSensitive: false, isHighlighted: false, sortOrder: 1 },
      ],
      environment: [
        { name: "ASPNETCORE_ENVIRONMENT", value: "Development", isFromSpec: true },
        { name: "ConnectionStrings__postgres", value: "Host=localhost;Port=5432;Username=postgres;Password=p@ssw0rd-pg", isFromSpec: false },
      ],
      healthReports: [{ status: "Healthy", key: "self", description: "Liveness probe succeeded." }],
      commands: [...defaultCommands("Running"), manyInputsCommand(), ...interactionContentCommands(), ...commandResultCommands()],
      relationships: [{ resourceName: "postgres", type: "Reference" }],
      isHidden: false,
      supportsDetailedTelemetry: true,
      iconName: "Window",
      iconVariant: "regular",
    },
    {
      name: "cache",
      resourceType: "Container",
      displayName: "cache",
      uid: "uid-cache",
      state: "Running",
      stateStyle: "success",
      health: "Healthy",
      createdAt: isoMinutesAgo(13),
      startedAt: isoMinutesAgo(13),
      stoppedAt: null,
      urls: [
        { name: "tcp", url: "tcp://localhost:6379", isInternal: false, isInactive: false, displayName: "tcp", sortOrder: 0 },
      ],
      properties: [
        { name: "resource.parentName", displayName: "Parent", value: "postgres", isSensitive: false, isHighlighted: false, sortOrder: -1 },
        { name: "container.image", displayName: "Image", value: "docker.io/library/redis:7.4", isSensitive: false, isHighlighted: true, sortOrder: 0 },
        { name: "container.id", displayName: "Container ID", value: "a1b2c3d4e5f6", isSensitive: false, isHighlighted: false, sortOrder: 1 },
        { name: "container.ports", displayName: "Ports", value: "6379/tcp", isSensitive: false, isHighlighted: false, sortOrder: 2 },
      ],
      environment: [
        { name: "REDIS_ARGS", value: "--requirepass p@ssw0rd-redis", isFromSpec: true },
      ],
      healthReports: [{ status: "Healthy", key: "redis", description: "PING returned PONG." }],
      commands: defaultCommands("Running"),
      relationships: [],
      isHidden: false,
      supportsDetailedTelemetry: false,
      iconName: "Database",
      iconVariant: "filled",
    },
    {
      name: "postgres",
      resourceType: "Container",
      displayName: "postgres",
      uid: "uid-postgres",
      state: "Running",
      stateStyle: "success",
      health: "Degraded",
      createdAt: isoMinutesAgo(13),
      startedAt: isoMinutesAgo(13),
      stoppedAt: null,
      urls: [
        { name: "tcp", url: "tcp://localhost:5432", isInternal: false, isInactive: false, displayName: "tcp", sortOrder: 0 },
      ],
      properties: [
        { name: "container.image", displayName: "Image", value: "docker.io/library/postgres:17.2", isSensitive: false, isHighlighted: true, sortOrder: 0 },
        { name: "container.id", displayName: "Container ID", value: "f6e5d4c3b2a1", isSensitive: false, isHighlighted: false, sortOrder: 1 },
      ],
      environment: [
        { name: "POSTGRES_USER", value: "postgres", isFromSpec: true },
        { name: "POSTGRES_PASSWORD", value: "p@ssw0rd-pg", isFromSpec: true },
      ],
      healthReports: [
        { status: "Degraded", key: "npgsql", description: "Connection pool nearing saturation (18/20)." },
      ],
      commands: defaultCommands("Running"),
      relationships: [],
      isHidden: false,
      supportsDetailedTelemetry: false,
      iconName: "Database",
      iconVariant: "regular",
    },
    {
      name: "migration",
      resourceType: "Executable",
      displayName: "migration",
      uid: "uid-migration",
      state: "Exited",
      stateStyle: "info",
      health: null,
      createdAt: isoMinutesAgo(12),
      startedAt: isoMinutesAgo(12),
      stoppedAt: isoMinutesAgo(11),
      urls: [],
      properties: [
        { name: "executable.path", displayName: "Executable", value: "/usr/local/share/dotnet/dotnet", isSensitive: false, isHighlighted: true, sortOrder: 0 },
        { name: "executable.exitCode", displayName: "Exit code", value: "0", isSensitive: false, isHighlighted: false, sortOrder: 1 },
      ],
      environment: [
        { name: "ConnectionStrings__postgres", value: "Host=localhost;Port=5432;Username=postgres;Password=p@ssw0rd-pg", isFromSpec: false },
      ],
      healthReports: [],
      commands: defaultCommands("Exited"),
      relationships: [{ resourceName: "postgres", type: "Reference" }],
      isHidden: false,
      supportsDetailedTelemetry: false,
      iconName: "Code",
      iconVariant: "regular",
      hasTerminal: true,
      terminalReplicaIndex: 0,
    },
    {
      name: "hiddenContainer",
      resourceType: "Container",
      displayName: "hiddenContainer",
      uid: "uid-hidden-container",
      state: null,
      stateStyle: null,
      health: null,
      createdAt: isoMinutesAgo(10),
      startedAt: isoMinutesAgo(10),
      stoppedAt: null,
      urls: [
        { name: "admin", url: "https://hidden.example.test/admin", isInternal: false, isInactive: false, displayName: "admin", sortOrder: 0 },
        { name: "diagnostics", url: "https://hidden.example.test/diagnostics/this/is/a/very/long/path/that/must/not/expand/the/resource/table", isInternal: false, isInactive: false, displayName: "diagnostics-with-a-very-long-display-name", sortOrder: 1 },
        { name: "metrics", url: "https://hidden.example.test/metrics", isInternal: false, isInactive: false, displayName: "metrics", sortOrder: 2 },
        { name: "internal", url: "http://hidden.internal:8080", isInternal: true, isInactive: false, displayName: "internal", sortOrder: 3 },
        { name: "inactive", url: "https://hidden.example.test/inactive", isInternal: false, isInactive: true, displayName: "inactive", sortOrder: 4 },
      ],
      properties: [
        { name: "container.image", displayName: "Image", value: "docker.io/library/busybox:1.37", isSensitive: false, isHighlighted: true, sortOrder: 0 },
      ],
      environment: [],
      healthReports: [],
      commands: defaultCommands("Unknown"),
      relationships: [],
      isHidden: true,
      supportsDetailedTelemetry: false,
      iconName: "Box",
      iconVariant: "regular",
    },
    {
      name: "insertionrows",
      resourceType: "Parameter",
      displayName: "insertionrows",
      uid: "uid-insertionrows",
      state: "Running",
      stateStyle: "success",
      health: null,
      createdAt: isoMinutesAgo(12),
      startedAt: isoMinutesAgo(12),
      stoppedAt: null,
      urls: [],
      properties: [
        { name: "Value", displayName: "Value", value: "1000", isSensitive: false, isHighlighted: true, sortOrder: 0 },
      ],
      environment: [],
      healthReports: [],
      commands: [
        { name: "parameter-set", displayName: "Set parameter", displayDescription: "Set the parameter value.", confirmationMessage: null, iconName: "Edit", iconVariant: "regular", isHighlighted: true, state: "enabled" },
      ],
      relationships: [],
      isHidden: false,
      supportsDetailedTelemetry: false,
      iconName: "Settings",
      iconVariant: "regular",
    },
    {
      name: "apikey",
      resourceType: "Parameter",
      displayName: "apikey",
      uid: "uid-apikey",
      state: "Running",
      stateStyle: "success",
      health: null,
      createdAt: isoMinutesAgo(12),
      startedAt: isoMinutesAgo(12),
      stoppedAt: null,
      urls: [],
      properties: [
        { name: "Value", displayName: "Value", value: "sk-9f2b7c1e4a8d", isSensitive: true, isHighlighted: true, sortOrder: 0 },
      ],
      environment: [],
      healthReports: [],
      commands: [
        { name: "parameter-set", displayName: "Set parameter", displayDescription: "Set the parameter value.", confirmationMessage: null, iconName: "Edit", iconVariant: "regular", isHighlighted: true, state: "enabled" },
      ],
      relationships: [],
      isHidden: false,
      supportsDetailedTelemetry: false,
      iconName: "Settings",
      iconVariant: "regular",
    },
    {
      name: "greeting",
      resourceType: "Parameter",
      displayName: "greeting",
      uid: "uid-greeting",
      state: "ValueMissing",
      stateStyle: "warning",
      health: null,
      createdAt: isoMinutesAgo(12),
      startedAt: null,
      stoppedAt: null,
      urls: [],
      properties: [
        { name: "Value", displayName: "Value", value: "Parameter resource could not be used because configuration key 'Parameters:greeting' is missing and the Parameter has no default value.", isSensitive: false, isHighlighted: true, sortOrder: 0 },
      ],
      environment: [],
      healthReports: [],
      commands: [
        { name: "parameter-set", displayName: "Set parameter", displayDescription: "Set the parameter value.", confirmationMessage: null, iconName: "Edit", iconVariant: "regular", isHighlighted: true, state: "enabled" },
      ],
      relationships: [],
      isHidden: false,
      supportsDetailedTelemetry: false,
      iconName: "Settings",
      iconVariant: "regular",
    },
  ];
}

function defaultCommands(state: string): Resource["commands"] {
  const running = state === "Running";
  return [
    {
      name: "resource-start",
      displayName: "Start",
      displayDescription: "Start the resource.",
      confirmationMessage: null,
      iconName: "Play",
      iconVariant: "filled",
      isHighlighted: !running,
      state: running ? "disabled" : "enabled",
    },
    {
      name: "resource-stop",
      displayName: "Stop",
      displayDescription: "Stop the resource.",
      confirmationMessage: "Are you sure you want to stop this resource?",
      iconName: "Stop",
      iconVariant: "filled",
      isHighlighted: false,
      state: running ? "enabled" : "disabled",
    },
    {
      name: "resource-restart",
      displayName: "Restart",
      displayDescription: "Restart the resource.",
      confirmationMessage: "Are you sure you want to restart this resource?",
      iconName: "ArrowClockwise",
      iconVariant: "regular",
      isHighlighted: running,
      state: running ? "enabled" : "disabled",
    },
    {
      name: "scale",
      displayName: "Scale…",
      displayDescription: "Set the replica count (prompts for input).",
      confirmationMessage: null,
      iconName: "ArrowClockwise",
      iconVariant: "regular",
      isHighlighted: false,
      state: "enabled",
    },
  ];
}

function manyInputsCommand(): ResourceCommand {
  return {
    name: "many-inputs",
    displayName: "Many inputs…",
    displayDescription: "Open a scrollable command with 50 inputs.",
    confirmationMessage: null,
    iconName: "TableLightning",
    iconVariant: "regular",
    isHighlighted: false,
    state: "enabled",
  };
}

function interactionContentCommands(): ResourceCommand[] {
  return [
    {
      name: "message-box-sample",
      displayName: "Review deployment…",
      displayDescription: "Open a warning message box with Markdown content.",
      confirmationMessage: null,
      iconName: "Warning",
      iconVariant: "regular",
      isHighlighted: false,
      state: "enabled",
    },
    {
      name: "notification-samples",
      displayName: "Show notification samples",
      displayDescription: "Show complete success and error notification variants.",
      confirmationMessage: null,
      iconName: "Info",
      iconVariant: "regular",
      isHighlighted: false,
      state: "enabled",
    },
  ];
}

function commandResultCommands(): ResourceCommand[] {
  return [
    {
      name: "result-text",
      displayName: "Show text result",
      displayDescription: "Return a downloadable plain-text command result.",
      confirmationMessage: null,
      iconName: "DocumentText",
      iconVariant: "regular",
      isHighlighted: true,
      state: "enabled",
    },
    {
      name: "result-json",
      displayName: "Show JSON result",
      displayDescription: "Return a formatted JSON command result.",
      confirmationMessage: null,
      iconName: "Braces",
      iconVariant: "regular",
      isHighlighted: true,
      state: "enabled",
    },
    {
      name: "result-markdown",
      displayName: "Show Markdown result",
      displayDescription: "Open a Markdown command result immediately.",
      confirmationMessage: null,
      iconName: "DocumentBulletList",
      iconVariant: "regular",
      isHighlighted: true,
      state: "enabled",
    },
    {
      name: "result-hidden",
      displayName: "Hidden result command",
      displayDescription: "This command must never appear in dashboard command surfaces.",
      confirmationMessage: null,
      iconName: "Document",
      iconVariant: "regular",
      isHighlighted: true,
      state: "hidden",
    },
  ];
}

const canvases: CanvasManifest[] = [
  {
    id: "resource-radar",
    title: "Resource Radar",
    description: "A sample canvas: live resource health and telemetry counters, authored as a sandboxed HTML panel that talks to Deck over the canvas bridge.",
    icon: "📡",
    entry: "sample-canvas.html",
    // Loaded relative to the app base so it works under file:// in Tauri and http in dev.
    url: "sample-canvas.html",
  },
  {
    id: "service-topology",
    title: "Service Topology",
    description: "Live node-graph of resources and their relationships, colored by health.",
    icon: "🕸️",
    entry: "service-topology.html",
    url: "service-topology.html",
  },
];

// Two attached AppHosts so the switcher is exercisable in browser/mock mode.
const mockApphosts: AppHostInfo[] = [
  { id: "local", name: "TestShop", resourceServiceUrl: "https://localhost:17042", state: "connected", active: true },
  { id: "demo-2", name: "OrdersService", resourceServiceUrl: "https://localhost:18055", state: "connected", active: false },
];

const metricDefs: Array<{ name: string; unit: string | null; resource: string; base: number; jitter: number; kind: MetricKind }> = [
  { name: "http.server.request.duration", unit: "ms", resource: "frontend", base: 42, jitter: 18, kind: "histogram" },
  { name: "http.server.active_requests", unit: "{request}", resource: "frontend", base: 7, jitter: 6, kind: "upDownCounter" },
  { name: "http.server.requests", unit: "{request}", resource: "frontend", base: 0, jitter: 0, kind: "counter" },
  { name: "http.client.request.duration", unit: "ms", resource: "apiservice", base: 28, jitter: 12, kind: "histogram" },
  { name: "db.client.connections.usage", unit: "{connection}", resource: "postgres", base: 14, jitter: 4, kind: "upDownCounter" },
  { name: "process.runtime.dotnet.gc.heap.size", unit: "By", resource: "apiservice", base: 33_554_432, jitter: 4_194_304, kind: "gauge" },
  { name: "cache.hit_ratio", unit: "1", resource: "cache", base: 0.92, jitter: 0.06, kind: "gauge" },
];

const logBodies = [
  "Request starting HTTP/2 GET /api/products",
  "Executed endpoint 'Products.GetAll'",
  "Request finished HTTP/2 GET /api/products - 200",
  "Cache hit for key 'products:all'",
  "Saved 3 changes to database",
  "Background reconciliation completed in 12ms",
];

const errorBodies = [
  "Connection pool nearing saturation (18/20)",
  "Retrying upstream call to apiservice (attempt 2)",
];

const spanNames = [
  "GET /api/products",
  "POST /api/orders",
  "products.query",
  "redis GET",
  "npgsql SELECT",
];

const MAX_RETAINED_TELEMETRY_RECORDS = 5_000;

class MockBackend {
  private resources: Resource[] = makeResources();
  private telemetry: TelemetrySummary = {
    logCount: 0,
    spanCount: 0,
    metricCount: metricDefs.length,
    recentLogs: [],
    recentSpans: [],
    metrics: metricDefs.map<MetricSummary>((m) => ({
      name: m.name,
      description: `Synthetic ${m.name} telemetry.`,
      meterName: "Aspire.Mock",
      unit: m.unit,
      resourceName: m.resource,
      kind: m.kind,
      lastValue: m.base,
      pointCount: 1,
    })),
  };
  private logCountByResource = new Map<string, number>();
  private telemetryTick = 0;

  // Per-metric timestamped history so the Metrics chart has a real time series.
  // Each entry holds the raw value samples; for histograms we also keep synthetic
  // p50/p90/p99 so the percentile chart has something to draw.
  private metricHistory = new Map<string, { t: number[]; v: number[]; p50: number[]; p90: number[]; p99: number[]; counter: number }>();

  private resourceSubs = new Set<(e: ResourcesEvent) => void>();
  private connectionSubs = new Set<(s: ConnectionStatus) => void>();
  private telemetrySubs = new Set<(t: TelemetrySummary) => void>();
  private apphostSubs = new Set<(a: AppHostInfo[]) => void>();
  private interactionSubs = new Set<(list: InteractionInfo[]) => void>();
  private dialog: InteractionInfo | null = null;
  private parameterDialogResourceName: string | null = null;
  private scaleUpdateVersion = 0;
  private manyInputsResourceName: string | null = null;
  private messageBoxResourceName: string | null = null;
  private notifications: InteractionInfo[] = [
    {
      interactionId: 9001, kind: "notification", title: "Unresolved parameters",
      message: "There are unresolved parameters that need to be set.",
      primaryButtonText: "Enter values", secondaryButtonText: "No",
      showSecondaryButton: true, showDismiss: true, enableMessageMarkdown: false,
      intent: "warning", inputs: [], linkText: "", linkUrl: "",
    },
  ];
  private consoleSubs = new Map<string, Set<(e: ConsoleLogEvent) => void>>();
  private consoleLineCounters = new Map<string, number>();
  private spanCountByResource = new Map<string, number>();

  private timers: ReturnType<typeof setInterval>[] = [];
  private started = false;

  private ensureStarted(): void {
    if (this.started) {
      return;
    }
    this.started = true;

    // Seed a few traces/logs so the pages aren't empty on first paint.
    const seedNow = Date.now();
    for (let i = 0; i < 4; i++) {
      // Seed realistic spacing so cursor and drag-to-zoom interactions work on
      // first paint instead of waiting for the first live polling interval.
      this.tickTelemetry(seedNow - (3 - i) * 1500);
    }

    // Telemetry grows steadily so charts and tables animate.
    this.timers.push(setInterval(() => this.tickTelemetry(), 1500));
    // Occasional resource state flips to exercise live deltas.
    this.timers.push(setInterval(() => this.tickResourceState(), 9000));
    // Console log streaming for subscribed resources.
    this.timers.push(setInterval(() => this.tickConsole(), 1200));
  }

  getConfig(): DeckConfig {
    return config;
  }

  getManageData(): ManageDataResponse {
    return {
      resources: this.resources
        .filter((resource) => !resource.isHidden)
        .map<ManageDataResponse["resources"][number]>((resource) => ({
          name: resource.name,
          displayName: resource.displayName,
          dataTypes: ["ResourceDetails", "ConsoleLogs", "StructuredLogs", "Traces", "Metrics"],
        })),
      isImportEnabled: true,
    };
  }

  exportManageData(request: ManageDataRequest): ManageDataExport {
    return {
      fileName: "aspire-telemetry-export-mock.json",
      blob: new Blob([JSON.stringify(request, null, 2)], { type: "application/json" }),
    };
  }

  importManageData(_file: File): void {
    // The browser playground has no persistent telemetry store to import into.
  }

  removeManageData(request: ManageDataRequest): void {
    for (const selection of request.resources) {
      if (selection.dataTypes.includes("StructuredLogs")) this.clearStructuredLogs(selection.resourceName);
      if (selection.dataTypes.includes("Traces")) this.clearTraces(selection.resourceName);
      if (selection.dataTypes.includes("Metrics")) this.clearMetrics(selection.resourceName);
    }
  }

  getAssistantInfo(): AssistantInfo {
    return { models: [{ family: "gpt-5.4", displayName: "GPT-5.4" }, { family: "gpt-4.1", displayName: "GPT-4.1" }] };
  }

  async streamAssistantChat(
    request: AssistantChatRequest,
    onEvent: (event: AssistantEvent) => void,
    signal: AbortSignal,
  ): Promise<void> {
    onEvent({ type: "start", content: null, message: null });
    const prompt = request.messages.at(-1)?.content ?? "";
    const chunks = ["I inspected ", "the dashboard context. ", `Your request was: ${prompt}`];
    for (const [index, content] of chunks.entries()) {
      await new Promise<void>((resolve, reject) => {
        // Keep the first mock chunk pending long enough for the playground to
        // exercise cancellation reliably under a loaded browser test worker.
        const timer = window.setTimeout(resolve, index === 0 ? 1_000 : 80);
        signal.addEventListener("abort", () => {
          window.clearTimeout(timer);
          reject(new DOMException("The operation was aborted.", "AbortError"));
        }, { once: true });
      });
      onEvent({ type: "content", content, message: null });
    }
    onEvent({ type: "complete", content: null, message: null });
  }

  listResources(): Resource[] {
    return structuredClone(this.resources);
  }

  listCanvases(): CanvasManifest[] {
    return structuredClone(canvases);
  }

  listApphosts(): AppHostInfo[] {
    return structuredClone(mockApphosts);
  }

  selectApphost(id: string): void {
    for (const a of mockApphosts) {
      a.active = a.id === id;
    }
    for (const cb of this.apphostSubs) {
      cb(structuredClone(mockApphosts));
    }
  }

  onApphosts(cb: (a: AppHostInfo[]) => void): Unsubscribe {
    this.apphostSubs.add(cb);
    cb(structuredClone(mockApphosts));
    return () => this.apphostSubs.delete(cb);
  }

  getTelemetrySummary(): TelemetrySummary {
    return structuredClone(this.telemetry);
  }

  clearStructuredLogs(resourceName: string | null): void {
    if (resourceName === null) {
      this.telemetry.logCount = 0;
      this.telemetry.recentLogs = [];
      this.logCountByResource.clear();
    } else {
      this.telemetry.logCount = Math.max(
        0,
        this.telemetry.logCount - (this.logCountByResource.get(resourceName) ?? 0),
      );
      this.telemetry.recentLogs = this.telemetry.recentLogs.filter(
        (log) => log.resourceName !== resourceName,
      );
      this.logCountByResource.delete(resourceName);
    }

    const snapshot = this.getTelemetrySummary();
    for (const callback of this.telemetrySubs) {
      callback(snapshot);
    }
  }

  clearTraces(resourceName: string | null): void {
    if (resourceName === null) {
      this.telemetry.spanCount = 0;
      this.telemetry.recentSpans = [];
      this.spanCountByResource.clear();
    } else {
      this.telemetry.spanCount = Math.max(
        0,
        this.telemetry.spanCount - (this.spanCountByResource.get(resourceName) ?? 0),
      );
      this.telemetry.recentSpans = this.telemetry.recentSpans.filter(
        (span) => span.resourceName !== resourceName,
      );
      this.spanCountByResource.delete(resourceName);
    }

    const snapshot = this.getTelemetrySummary();
    for (const callback of this.telemetrySubs) {
      callback(snapshot);
    }
  }

  clearMetrics(resourceName: string | null): void {
    const removed = resourceName === null
      ? this.telemetry.metrics
      : this.telemetry.metrics.filter((metric) => metric.resourceName === resourceName);
    const removedNames = new Set(removed.map((metric) => metric.name));
    this.telemetry.metrics = resourceName === null
      ? []
      : this.telemetry.metrics.filter((metric) => metric.resourceName !== resourceName);
    this.telemetry.metricCount = this.telemetry.metrics.reduce(
      (total, metric) => total + metric.pointCount,
      0,
    );
    for (const name of removedNames) {
      this.metricHistory.delete(name);
    }

    const snapshot = this.getTelemetrySummary();
    for (const callback of this.telemetrySubs) {
      callback(snapshot);
    }
  }

  getMetricSeries(query: MetricSeriesQuery): MetricSeriesResponse | null {
    const def = metricDefs.find((m) => m.name === query.name);
    const hist = this.metricHistory.get(query.name);
    if (!def || !hist || hist.t.length === 0) {
      return null;
    }
    const windowMs = (query.windowSeconds ?? 300) * 1000;
    const cutoff = hist.t[hist.t.length - 1]! - windowMs;
    const idx = hist.t.findIndex((t) => t >= cutoff);
    const start = idx < 0 ? 0 : idx;

    const ts = hist.t.slice(start);
    const base: MetricSeriesResponse = {
      name: def.name,
      meterName: "Aspire.Mock",
      resourceName: def.resource,
      unit: def.unit,
      kind: def.kind,
      timestampsMs: ts,
    };

    if (def.kind === "histogram") {
      const methods = query.dimensions?.["http.method"];
      const factor = methods?.length === 1 && methods[0] === "GET" ? 0.8
        : methods?.length === 1 && methods[0] === "POST" ? 1.2
          : methods?.length === 0 ? 0 : 1;
      const scale = (values: number[]) => values.slice(start).map((value) => value * factor);
      const histogramMode = query.histogramMode ?? (query.showCount ? "count" : "percentiles");
      if (histogramMode === "count") {
        return {
          ...base,
          values: scale(hist.v),
          dimensionFilters: [{ name: "http.method", values: ["GET", "POST"] }],
          dimensions: [],
          exemplars: [],
          hasOverflow: false,
          showCount: true,
          histogramMode,
        };
      }
      if (histogramMode === "sum") {
        return {
          ...base,
          sum: scale(hist.v).map((value) => value * 10),
          dimensionFilters: [{ name: "http.method", values: ["GET", "POST"] }],
          dimensions: [],
          exemplars: [],
          hasOverflow: false,
          histogramMode,
        };
      }
      if (histogramMode === "buckets") {
        return {
          ...base,
          bucketBounds: [25, 50, 100],
          buckets: [
            { upperBound: 25, values: scale(hist.v).map((value) => Math.round(value * 0.2)) },
            { upperBound: 50, values: scale(hist.v).map((value) => Math.round(value * 0.35)) },
            { upperBound: 100, values: scale(hist.v).map((value) => Math.round(value * 0.3)) },
            { upperBound: null, values: scale(hist.v).map((value) => Math.round(value * 0.15)) },
          ],
          dimensionFilters: [{ name: "http.method", values: ["GET", "POST"] }],
          dimensions: [],
          exemplars: [],
          hasOverflow: false,
          histogramMode,
        };
      }
      return {
        ...base,
        p50: scale(hist.p50),
        p90: scale(hist.p90),
        p99: scale(hist.p99),
        dimensionFilters: [{ name: "http.method", values: ["GET", "POST"] }],
        dimensions: [
          {
            attributes: [{ key: "http.method", value: "GET" }],
            timestampsMs: ts,
            p50: hist.p50.slice(start).map((value) => value * 0.8),
            p90: hist.p90.slice(start).map((value) => value * 0.8),
            p99: hist.p99.slice(start).map((value) => value * 0.8),
          },
          {
            attributes: [{ key: "http.method", value: "POST" }],
            timestampsMs: ts,
            p50: hist.p50.slice(start).map((value) => value * 1.2),
            p90: hist.p90.slice(start).map((value) => value * 1.2),
            p99: hist.p99.slice(start).map((value) => value * 1.2),
          },
        ],
        exemplars: ts.length === 0 ? [] : [{
          timestampMs: ts[ts.length - 1]!,
          value: hist.p99[hist.p99.length - 1]!,
          traceId: "00000000000000000000000000000001",
          spanId: "0000000000000001",
          attributes: [{ key: "http.method", value: "POST" }],
        }],
        hasOverflow: false,
        showCount: false,
        histogramMode,
      };
    }
    if (def.kind === "counter") {
      // Convert the cumulative samples to a per-second rate between points.
      const rateT: number[] = [];
      const rateV: number[] = [];
      for (let i = start + 1; i < hist.t.length; i++) {
        const dt = (hist.t[i]! - hist.t[i - 1]!) / 1000;
        if (dt <= 0) {
          continue;
        }
        rateT.push(hist.t[i]!);
        rateV.push(Math.max(0, (hist.v[i]! - hist.v[i - 1]!) / dt));
      }
      return { ...base, timestampsMs: rateT, values: rateV };
    }
    return { ...base, values: hist.v.slice(start) };
  }

  executeCommand(args: ExecuteCommandArgs): CommandResponse {
    const target = this.resources.find((r) => r.name === args.resourceName);
    if (!target) {
      return { kind: "failed", message: `Resource '${args.resourceName}' not found.`, result: null };
    }

    switch (args.commandName) {
      case "resource-start":
      case "resource-restart":
        this.setResourceState(target, "Running", "success", "Healthy");
        break;
      case "resource-stop":
        this.setResourceState(target, "Exited", "info", null);
        break;
      case "scale":
        // Custom command that prompts for inputs (demonstrates the interaction pane).
        this.dialog = this.buildScaleDialog([]);
        this.emitInteractions();
        return { kind: "succeeded", message: "Awaiting input…", result: null };
      case "parameter-set":
        this.parameterDialogResourceName = target.name;
        this.dialog = this.buildParameterDialog(target);
        this.emitInteractions();
        return { kind: "succeeded", message: "Awaiting input…", result: null };
      case "many-inputs":
        this.manyInputsResourceName = target.name;
        this.dialog = this.buildManyInputsDialog();
        this.emitInteractions();
        return { kind: "succeeded", message: "Awaiting input…", result: null };
      case "message-box-sample":
        this.messageBoxResourceName = target.name;
        this.dialog = this.buildMessageBox();
        this.emitInteractions();
        return { kind: "succeeded", message: "Awaiting confirmation…", result: null };
      case "notification-samples":
        this.notifications = this.notifications.filter((notification) => ![9101, 9102].includes(notification.interactionId));
        this.notifications.push(
          {
            interactionId: 9101, kind: "notification", title: "Deployment complete",
            message: "**Deployment complete** for `apiservice`. Read the [release notes](https://example.com/release) or [unsafe](javascript:alert(1)).",
            primaryButtonText: "Review", secondaryButtonText: "Later", showSecondaryButton: true,
            showDismiss: false, enableMessageMarkdown: true, intent: "success", inputs: [],
            linkText: "Open runbook", linkUrl: "https://example.com/runbook",
          },
          {
            interactionId: 9102, kind: "notification", title: "Deployment warning",
            message: "A deployment health check needs attention.", primaryButtonText: "",
            secondaryButtonText: "", showSecondaryButton: false, showDismiss: true,
            enableMessageMarkdown: false, intent: "error", inputs: [], linkText: "", linkUrl: "",
          },
        );
        this.emitInteractions();
        return { kind: "succeeded", message: "Notifications shown.", result: null };
      case "result-text":
        return {
          kind: "succeeded",
          message: "Text report generated.",
          result: { value: "Deployment report\nStatus: Healthy\nReplicas: 3", format: "text", displayImmediately: false },
        };
      case "result-json":
        return {
          kind: "succeeded",
          message: "JSON report generated.",
          result: { value: '{"status":"Healthy","replicas":3,"regions":["west","east"]}', format: "json", displayImmediately: false },
        };
      case "result-markdown":
        return {
          kind: "succeeded",
          message: "Markdown report generated.",
          result: {
            value: "## Deployment report\n\n| Region | Status |\n| --- | --- |\n| West | **Healthy** |\n| East | Healthy |\n\n[Runbook](https://example.com/runbook) [unsafe](javascript:alert(1))",
            format: "markdown",
            displayImmediately: true,
          },
        };
      default:
        return { kind: "undefined", message: `Unknown command '${args.commandName}'.`, result: null };
    }

    return { kind: "succeeded", message: `Command '${args.commandName}' executed on '${args.resourceName}'.`, result: null };
  }

  // --- Interactions (mock) ---

  private buildScaleDialog(
    errorsFor: { name: string; error: string }[],
    values: Record<string, string> = {},
    regionLoading = false,
  ): InteractionInfo {
    const errs = (name: string): string[] => errorsFor.filter((e) => e.name === name).map((e) => e.error);
    const tier = values.tier ?? "standard";
    const regionOptions: [string, string][] = tier === "premium"
      ? [["global", "Global"], ["edge", "Edge"]]
      : [["east", "US East"], ["west", "US West"]];
    const requestedRegion = values.region ?? regionOptions[0]![0];
    const region = regionOptions.some(([value]) => value === requestedRegion)
      ? requestedRegion
      : regionOptions[0]![0];
    const inputs: InteractionInputInfo[] = [
      {
        name: "replicas", label: "Replicas", placeholder: "1-10", inputType: "number", required: true,
        options: [], value: values.replicas ?? "1", validationErrors: errs("replicas"), description: "Number of instances to run.",
        enableDescriptionMarkdown: false, maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: true,
      },
      {
        name: "tier", label: "Tier", placeholder: "", inputType: "choice", required: true,
        options: [["standard", "Standard"], ["premium", "Premium"]], value: tier,
        validationErrors: errs("tier"), description: "Compute **tier** for the replicas. See the [scaling guide](https://example.com/scaling).",
        enableDescriptionMarkdown: true, maxLength: 0, allowCustomChoice: true, disabled: false, updateStateOnChange: true,
      },
      {
        name: "region", label: "Region", placeholder: regionLoading ? "Loading regions…" : "Select a region",
        inputType: "choice", required: true, options: regionLoading ? [] : regionOptions,
        value: regionLoading ? "" : region, validationErrors: errs("region"), description: "Available regions depend on the selected tier.",
        enableDescriptionMarkdown: false, maxLength: 0, allowCustomChoice: false, disabled: regionLoading, updateStateOnChange: false,
      },
      {
        name: "drain", label: "Drain connections before scaling down", placeholder: "", inputType: "boolean",
        required: false, options: [], value: values.drain ?? "true", validationErrors: [], description: "",
        enableDescriptionMarkdown: false, maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
      },
    ];
    return {
      interactionId: 1, kind: "inputsDialog", title: "Scale resource",
      message: "Choose how many replicas to run.", primaryButtonText: "Scale", secondaryButtonText: "Cancel",
      showSecondaryButton: true, showDismiss: true, enableMessageMarkdown: false, intent: "none",
      inputs, linkText: "", linkUrl: "",
    };
  }

  private buildParameterDialog(resource: Resource, validationErrors: string[] = []): InteractionInfo {
    const property = resource.properties.find((candidate) => candidate.name === PARAMETER_VALUE_PROPERTY);
    const value = resource.state === "ValueMissing" ? "" : String(property?.value ?? "");
    return {
      interactionId: 2,
      kind: "inputsDialog",
      title: `Set ${resource.displayName}`,
      message: "Enter the parameter value for this AppHost session.",
      primaryButtonText: "Set",
      secondaryButtonText: "Cancel",
      showSecondaryButton: true,
      showDismiss: true,
      enableMessageMarkdown: false,
      intent: "none",
      inputs: [{
        name: "value",
        label: "Value",
        placeholder: "Parameter value",
        inputType: property?.isSensitive ? "secretText" : "text",
        required: true,
        options: [],
        value,
        validationErrors,
        description: "",
        enableDescriptionMarkdown: false,
        maxLength: 0,
        allowCustomChoice: false,
        disabled: false,
        updateStateOnChange: false,
      }],
      linkText: "",
      linkUrl: "",
    };
  }

  private buildManyInputsDialog(): InteractionInfo {
    const inputs: InteractionInputInfo[] = Array.from({ length: 50 }, (_, index) => ({
      name: `input${index + 1}`,
      label: `Input ${index + 1}`,
      placeholder: `Enter input ${index + 1}`,
      inputType: "text",
      required: false,
      options: [],
      value: "",
      validationErrors: [],
      description: "",
      enableDescriptionMarkdown: false,
      maxLength: 0,
      allowCustomChoice: false,
      disabled: false,
      updateStateOnChange: false,
    }));
    return {
      interactionId: 3,
      kind: "inputsDialog",
      title: "Many inputs",
      message: "Complete any values and submit the entire 50-field form.",
      primaryButtonText: "Submit all",
      secondaryButtonText: "Cancel",
      showSecondaryButton: true,
      showDismiss: true,
      enableMessageMarkdown: false,
      intent: "none",
      inputs,
      linkText: "",
      linkUrl: "",
    };
  }

  private buildMessageBox(): InteractionInfo {
    return {
      interactionId: 4,
      kind: "messageBox",
      title: "Review deployment",
      message: "**Deployment warning**\n\nContinue only after reading the [review guide](https://example.com/review). HTML stays text: <script>alert('unsafe')</script>.",
      primaryButtonText: "Continue",
      secondaryButtonText: "Go back",
      showSecondaryButton: true,
      showDismiss: true,
      enableMessageMarkdown: true,
      intent: "warning",
      inputs: [],
      linkText: "",
      linkUrl: "",
    };
  }

  // Notifications stack alongside (and outlive) the one-at-a-time dialog.
  private interactionList(): InteractionInfo[] {
    return [...this.notifications, ...(this.dialog ? [this.dialog] : [])];
  }

  private emitInteractions(): void {
    const list = this.interactionList();
    for (const cb of this.interactionSubs) {
      cb(list);
    }
  }

  onInteractions(cb: (list: InteractionInfo[]) => void): Unsubscribe {
    this.interactionSubs.add(cb);
    cb(this.interactionList());
    return () => this.interactionSubs.delete(cb);
  }

  respondInteraction(interactionId: number, action: string, values: Record<string, string>): void {
    // A reply to a notification just dismisses that toast.
    if (this.notifications.some((n) => n.interactionId === interactionId)) {
      this.notifications = this.notifications.filter((n) => n.interactionId !== interactionId);
      this.emitInteractions();
      return;
    }

    if (interactionId === 2 && this.parameterDialogResourceName) {
      const target = this.resources.find((resource) => resource.name === this.parameterDialogResourceName);
      if (action === "submit" && target) {
        if (!values.value) {
          this.dialog = this.buildParameterDialog(target, ["Value is required."]);
          this.emitInteractions();
          return;
        }
        const property = target.properties.find((candidate) => candidate.name === PARAMETER_VALUE_PROPERTY);
        if (property) {
          property.value = values.value;
        }
        target.state = "Running";
        target.stateStyle = "success";
        target.startedAt = new Date().toISOString();
        this.emitResources({ type: "change", upserts: [structuredClone(target)] });
      }
      if (action !== "update") {
        this.parameterDialogResourceName = null;
        this.dialog = null;
        this.emitInteractions();
      }
      return;
    }

    if (interactionId === 3 && this.manyInputsResourceName) {
      const target = this.resources.find((resource) => resource.name === this.manyInputsResourceName);
      if (action === "submit" && target) {
        target.properties = target.properties.filter((property) => !property.name.startsWith("command.manyInputs."));
        target.properties.push(
          { name: "command.manyInputs.count", displayName: "Submitted input count", value: String(Object.keys(values).length), isSensitive: false, isHighlighted: false, sortOrder: 90 },
          { name: "command.manyInputs.last", displayName: "Last input value", value: values.input50 ?? "", isSensitive: false, isHighlighted: false, sortOrder: 91 },
        );
        this.emitResources({ type: "change", upserts: [structuredClone(target)] });
      }
      if (action !== "update") {
        this.manyInputsResourceName = null;
        this.dialog = null;
        this.emitInteractions();
      }
      return;
    }

    if (interactionId === 4 && this.messageBoxResourceName) {
      const target = this.resources.find((resource) => resource.name === this.messageBoxResourceName);
      if (target) {
        target.properties = target.properties.filter((property) => property.name !== "command.messageBox.result");
        target.properties.push({
          name: "command.messageBox.result",
          displayName: "Message box result",
          value: action === "primary" ? "primary" : action === "secondary" ? "secondary" : "dismissed",
          isSensitive: false,
          isHighlighted: false,
          sortOrder: 92,
        });
        this.emitResources({ type: "change", upserts: [structuredClone(target)] });
      }
      this.messageBoxResourceName = null;
      this.dialog = null;
      this.emitInteractions();
      return;
    }

    if (action === "submit" || action === "update") {
      const errors: { name: string; error: string }[] = [];
      const replicas = Number(values.replicas);
      if (!Number.isInteger(replicas) || replicas < 1 || replicas > 10) {
        errors.push({ name: "replicas", error: "Replicas must be a whole number between 1 and 10." });
      }
      if (action === "update") {
        const currentTier = this.dialog?.inputs.find((input) => input.name === "tier")?.value;
        if (values.tier !== currentTier) {
          const updateVersion = ++this.scaleUpdateVersion;
          this.dialog = this.buildScaleDialog(errors, values, true);
          this.emitInteractions();
          window.setTimeout(() => {
            if (this.dialog?.interactionId !== 1 || updateVersion !== this.scaleUpdateVersion) {
              return;
            }
            this.dialog = this.buildScaleDialog(errors, values);
            this.emitInteractions();
          }, 150);
          return;
        }
        this.dialog = this.buildScaleDialog(errors, values);
        this.emitInteractions();
        return;
      }
      if (errors.length > 0) {
        this.dialog = this.buildScaleDialog(errors, values);
        this.emitInteractions();
        return;
      }
      this.dialog = null;
      this.emitInteractions();
      return;
    }
    // cancel / dismiss
    this.dialog = null;
    this.emitInteractions();
  }

  onResources(cb: (e: ResourcesEvent) => void): Unsubscribe {
    this.ensureStarted();
    this.resourceSubs.add(cb);
    cb({ type: "snapshot", resources: this.listResources() });
    return () => this.resourceSubs.delete(cb);
  }

  onConnection(cb: (s: ConnectionStatus) => void): Unsubscribe {
    this.ensureStarted();
    this.connectionSubs.add(cb);
    // Simulate a brief connecting -> connected handshake for each target.
    const targets = ["resourceService", "otlpGrpc", "otlpHttp"] as const;
    for (const target of targets) {
      cb({ target, state: "connecting" });
      setTimeout(() => cb({ target, state: "connected", message: "Connected (mock)" }), 500 + Math.random() * 800);
    }
    return () => this.connectionSubs.delete(cb);
  }

  onTelemetry(cb: (t: TelemetrySummary) => void): Unsubscribe {
    this.ensureStarted();
    this.telemetrySubs.add(cb);
    cb(this.getTelemetrySummary());
    return () => this.telemetrySubs.delete(cb);
  }

  subscribeConsoleLogs(resourceName: string, cb: (e: ConsoleLogEvent) => void): Unsubscribe {
    this.ensureStarted();
    let set = this.consoleSubs.get(resourceName);
    if (!set) {
      set = new Set();
      this.consoleSubs.set(resourceName, set);
    }
    set.add(cb);

    // Replay a short backlog so the console isn't empty on open.
    const backlog: ConsoleLogEvent = { resourceName, lines: [] };
    for (let i = 0; i < 6; i++) {
      backlog.lines.push({
        lineNumber: this.nextConsoleLine(resourceName),
        text: `${new Date().toISOString()} ${logBodies[i % logBodies.length]}`,
        isStdErr: false,
      });
    }
    cb(backlog);

    return () => {
      const current = this.consoleSubs.get(resourceName);
      current?.delete(cb);
      if (current && current.size === 0) {
        this.consoleSubs.delete(resourceName);
      }
    };
  }

  private nextConsoleLine(resourceName: string): number {
    const next = (this.consoleLineCounters.get(resourceName) ?? 0) + 1;
    this.consoleLineCounters.set(resourceName, next);
    return next;
  }

  private setResourceState(
    target: Resource,
    state: string,
    stateStyle: string,
    health: string | null,
  ): void {
    target.state = state;
    target.stateStyle = stateStyle;
    target.health = health;
    target.commands = defaultCommands(state);
    if (state === "Running") {
      target.startedAt = new Date().toISOString();
      target.stoppedAt = null;
    } else {
      target.stoppedAt = new Date().toISOString();
    }
    this.emitResources({ type: "change", upserts: [structuredClone(target)] });
  }

  private emitResources(e: ResourcesEvent): void {
    for (const cb of this.resourceSubs) {
      cb(structuredClone(e));
    }
  }

  private tickResourceState(): void {
    // Flip postgres health between Healthy and Degraded to show live state.
    const postgres = this.resources.find((r) => r.name === "postgres");
    if (postgres) {
      const degraded = postgres.health === "Degraded";
      postgres.health = degraded ? "Healthy" : "Degraded";
      postgres.healthReports = [
        {
          status: postgres.health,
          key: "npgsql",
          description: degraded ? "Connection pool recovered (8/20)." : "Connection pool nearing saturation (18/20).",
        },
      ];
      this.emitResources({ type: "change", upserts: [structuredClone(postgres)] });
    }
  }

  private tickConsole(): void {
    for (const [resourceName, subs] of this.consoleSubs) {
      if (subs.size === 0) {
        continue;
      }
      const isErr = Math.random() < 0.15;
      const body = isErr
        ? errorBodies[Math.floor(Math.random() * errorBodies.length)]
        : logBodies[Math.floor(Math.random() * logBodies.length)];
      const event: ConsoleLogEvent = {
        resourceName,
        lines: [
          {
            lineNumber: this.nextConsoleLine(resourceName),
            text: `${new Date().toISOString()} ${body}`,
            isStdErr: isErr,
          },
        ],
      };
      for (const cb of subs) {
        cb(structuredClone(event));
      }
    }
  }

  private tickTelemetry(timestampMs = Date.now()): void {
    // Append a new log record.
    // Keep browser-mode telemetry representative and deterministic so visual and
    // interaction tests always exercise both successful and failed traces.
    const telemetryIndex = this.telemetryTick++;
    const isErr = telemetryIndex % 4 === 0;
    const isGenAI = telemetryIndex % 5 === 1;
    const resourceName = this.resources[telemetryIndex % 3]?.name ?? "frontend";
    const traceId = indexedHex(telemetryIndex + 1, 32);
    const spanIdBase = telemetryIndex * 8 + 1;
    const spanId = indexedHex(spanIdBase, 16);
    const log: LogRecordSummary = {
      timeUnixNano: toUnixNano(timestampMs),
      observedTimeUnixNano: toUnixNano(timestampMs),
      severity: isErr ? "Error" : "Information",
      severityNumber: isErr ? 17 : 9,
      body: isGenAI
        ? JSON.stringify({ content: "Summarize the latest catalog changes." })
        : isErr
        ? errorBodies[Math.floor(Math.random() * errorBodies.length)]!
        : logBodies[Math.floor(Math.random() * logBodies.length)]!,
      resourceName,
      traceId,
      spanId,
      parentId: null,
      eventName: isGenAI ? "gen_ai.user.message" : isErr ? "Catalog.RequestFailed" : "Catalog.RequestCompleted",
      originalFormat: null,
      scopeName: "Aspire.Deck.MockTelemetry",
      scopeVersion: "1.0.0",
      attributes: [
        ...(isGenAI ? [
          { key: "event.name", value: "gen_ai.user.message" },
          { key: "gen_ai.system", value: "openai" },
          { key: "gen_ai.request.model", value: "gpt-4.1-mini" },
        ] : []),
        { key: "http.request.method", value: isErr ? "POST" : "GET" },
        { key: "http.response.status_code", value: isErr ? "500" : "200" },
        ...(isErr
          ? [
              { key: "exception.type", value: "System.InvalidOperationException" },
              { key: "exception.message", value: "The simulated request failed." },
              { key: "exception.stacktrace", value: "at Catalog.RequestHandler.HandleAsync()" },
            ]
          : []),
      ],
      scopeAttributes: [{ key: "telemetry.auto.version", value: "1.0.0" }],
      resourceAttributes: [
        { key: "service.name", value: resourceName },
        { key: "service.instance.id", value: `${resourceName}-1` },
        { key: "deployment.environment.name", value: "Development" },
      ],
      flags: 1,
      droppedAttributesCount: 0,
      scopeDroppedAttributesCount: 0,
      resourceDroppedAttributesCount: 0,
    };
    this.telemetry.recentLogs = [log, ...this.telemetry.recentLogs].slice(0, MAX_RETAINED_TELEMETRY_RECORDS);
    this.telemetry.logCount += 1;
    this.logCountByResource.set(
      resourceName,
      (this.logCountByResource.get(resourceName) ?? 0) + 1,
    );

    // Append a nested trace: a server root span with a few staggered child spans
    // (and one grandchild) so the waterfall shows a realistic call timeline.
    const t0 = timestampMs - 220;
    const at = (offsetMs: number) => String(BigInt(t0 + offsetMs) * 1_000_000n);
    const durNanos = (ms: number) => String(Math.floor(ms * 1_000_000));
    const pick = <T,>(items: T[]) => items[Math.floor(Math.random() * items.length)]!;

    const parentId = spanId;
    const dbChildId = indexedHex(spanIdBase + 2, 16);
    const segments: {
      spanId: string;
      parentSpanId: string | null;
      name: string;
      kind: string;
      resource: string;
      start: number;
      dur: number;
      error?: boolean;
    }[] = [
      { spanId: parentId, parentSpanId: null, name: isGenAI ? "chat completion" : pick(spanNames), kind: "Server", resource: resourceName, start: 0, dur: 200 },
      { spanId: indexedHex(spanIdBase + 1, 16), parentSpanId: parentId, name: "redis GET", kind: "Client", resource: "cache", start: 12, dur: 18 },
      { spanId: dbChildId, parentSpanId: parentId, name: "products.query", kind: "Client", resource: "apiservice", start: 40, dur: 130, error: isErr },
      { spanId: indexedHex(spanIdBase + 3, 16), parentSpanId: dbChildId, name: "npgsql SELECT", kind: "Client", resource: "catalogdb", start: 55, dur: 95 },
      { spanId: indexedHex(spanIdBase + 4, 16), parentSpanId: parentId, name: "serialize", kind: "Internal", resource: resourceName, start: 178, dur: 18 },
    ];
    const newSpans: SpanSummary[] = segments.map((s) => ({
      traceId,
      spanId: s.spanId,
      traceState: "vendor=mock",
      parentSpanId: s.parentSpanId,
      flags: 1,
      name: s.name,
      kind: s.kind,
      resourceName: s.resource,
      startUnixNano: at(s.start),
      durationNanos: durNanos(s.dur),
      statusCode: s.error ? "Error" : "Ok",
      statusMessage: s.error ? "The simulated dependency failed." : null,
      scopeName: "Aspire.Deck.MockTelemetry",
      scopeVersion: "1.0.0",
      attributes: [
        { key: "code.function.name", value: s.name },
        { key: "server.address", value: s.resource },
        ...(isGenAI && s.parentSpanId === null ? [
          { key: "gen_ai.system", value: "openai" },
          { key: "gen_ai.operation.name", value: "chat" },
          { key: "gen_ai.request.model", value: "gpt-4.1-mini" },
          { key: "gen_ai.input.messages", value: JSON.stringify([{ role: "user", content: "Summarize the latest catalog changes." }]) },
          { key: "gen_ai.output.messages", value: JSON.stringify([{ role: "assistant", content: "The catalog added two products and updated pricing." }]) },
        ] : []),
        ...(s.name === "redis GET" || s.name === "npgsql SELECT"
          ? [{ key: "db.system.name", value: s.name === "redis GET" ? "redis" : "postgresql" }]
          : s.name === "products.query"
            ? [{ key: "rpc.system", value: "grpc" }]
            : s.kind === "Server"
              ? [{ key: "http.request.method", value: "GET" }]
              : []),
      ],
      scopeAttributes: [{ key: "telemetry.auto.version", value: "1.0.0" }],
      resourceAttributes: [
        { key: "service.name", value: s.resource },
        { key: "service.instance.id", value: `${s.resource}-1` },
        { key: "deployment.environment.name", value: "Development" },
      ],
      droppedAttributesCount: s.error ? 1 : 0,
      scopeDroppedAttributesCount: 0,
      resourceDroppedAttributesCount: 0,
      events: s.error
        ? [{
            timeUnixNano: at(s.start + s.dur),
            name: "exception",
            attributes: [
              { key: "exception.type", value: "System.InvalidOperationException" },
              { key: "exception.message", value: "The simulated dependency failed." },
            ],
            droppedAttributesCount: 0,
          }]
        : [],
      droppedEventsCount: 0,
      links: s.parentSpanId === null && telemetryIndex > 0
        ? [{
            traceId: indexedHex(telemetryIndex, 32),
            spanId: indexedHex((telemetryIndex - 1) * 8 + 1, 16),
            traceState: "vendor=mock",
            attributes: [{ key: "link.reason", value: "previous request" }],
            droppedAttributesCount: 0,
            flags: 1,
          }]
        : [],
      droppedLinksCount: 0,
    }));
    this.telemetry.recentSpans = [...newSpans, ...this.telemetry.recentSpans].slice(0, MAX_RETAINED_TELEMETRY_RECORDS);
    this.telemetry.spanCount += newSpans.length;
    for (const span of newSpans) {
      if (span.resourceName !== null) {
        this.spanCountByResource.set(
          span.resourceName,
          (this.spanCountByResource.get(span.resourceName) ?? 0) + 1,
        );
      }
    }

    // Advance each metric's last value with bounded jitter, and append a
    // timestamped sample to its history so the chart shows a real time series.
    const now = timestampMs;
    this.telemetry.metrics = this.telemetry.metrics.map((metric) => {
      const def = metricDefs.find((candidate) => candidate.name === metric.name)!;
      const prev = metric.lastValue ?? def.base;
      let next: number;
      if (def.kind === "counter") {
        // Monotonic cumulative counter: grows by a random per-tick increment.
        next = prev + Math.floor(Math.random() * 12);
      } else {
        next = prev + (Math.random() - 0.5) * def.jitter;
        if (def.unit === "1") {
          next = Math.max(0, Math.min(1, next)); // ratios within [0, 1]
        } else {
          next = Math.max(0, next);
        }
      }

      const hist = this.metricHistory.get(metric.name) ?? { t: [], v: [], p50: [], p90: [], p99: [], counter: 0 };
      hist.t.push(now);
      hist.v.push(next);
      if (def.kind === "histogram") {
        // Synthesize plausible latency percentiles around the base value.
        const p50 = next;
        hist.p50.push(p50);
        hist.p90.push(p50 * (1.6 + Math.random() * 0.3));
        hist.p99.push(p50 * (2.4 + Math.random() * 0.6));
      }
      // Bound history.
      const CAP = 4000;
      for (const arr of [hist.t, hist.v, hist.p50, hist.p90, hist.p99]) {
        if (arr.length > CAP) {
          arr.splice(0, arr.length - CAP);
        }
      }
      this.metricHistory.set(metric.name, hist);

      return { ...metric, lastValue: next, pointCount: metric.pointCount + 1 };
    });

    const snapshot = this.getTelemetrySummary();
    for (const cb of this.telemetrySubs) {
      cb(snapshot);
    }
  }
}

export const mockBackend = new MockBackend();
