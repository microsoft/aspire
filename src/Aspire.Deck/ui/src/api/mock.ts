// Rich, self-contained mock backend used when the UI runs outside Tauri
// (browser dev / `npm run preview`). It mirrors the command + event surface
// defined in CONTRACT.md so that App code is identical in both modes.

import type {
  AppHostInfo,
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
  Resource,
  ResourcesEvent,
  SpanSummary,
  TelemetrySummary,
} from "./types";

type Unsubscribe = () => void;

function nowNano(offsetMs = 0): string {
  // OTLP uses unix nanoseconds; keep as string to avoid bigint loss.
  return (BigInt(Date.now() + offsetMs) * 1_000_000n).toString();
}

function isoMinutesAgo(minutes: number): string {
  return new Date(Date.now() - minutes * 60_000).toISOString();
}

function randomHex(length: number): string {
  let out = "";
  const chars = "0123456789abcdef";
  for (let i = 0; i < length; i++) {
    out += chars[Math.floor(Math.random() * chars.length)];
  }
  return out;
}

const config: DeckConfig = {
  applicationName: "TestShop",
  resourceServiceUrl: "https://localhost:17042",
  otlpGrpcUrl: "https://localhost:18889",
  otlpHttpUrl: "https://localhost:18890",
  version: "9.0.0-dev (mock)",
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
      commands: defaultCommands("Running"),
      relationships: [{ resourceName: "postgres", type: "Reference" }],
      isHidden: false,
      supportsDetailedTelemetry: true,
      iconName: "Window",
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
      isHighlighted: !running,
      state: running ? "disabled" : "enabled",
    },
    {
      name: "resource-stop",
      displayName: "Stop",
      displayDescription: "Stop the resource.",
      confirmationMessage: "Are you sure you want to stop this resource?",
      iconName: "Stop",
      isHighlighted: false,
      state: running ? "enabled" : "disabled",
    },
    {
      name: "resource-restart",
      displayName: "Restart",
      displayDescription: "Restart the resource.",
      confirmationMessage: "Are you sure you want to restart this resource?",
      iconName: "ArrowClockwise",
      isHighlighted: running,
      state: running ? "enabled" : "disabled",
    },
    {
      name: "scale",
      displayName: "Scale…",
      displayDescription: "Set the replica count (prompts for input).",
      confirmationMessage: null,
      iconName: "ArrowClockwise",
      isHighlighted: false,
      state: "enabled",
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
      unit: m.unit,
      resourceName: m.resource,
      kind: m.kind,
      lastValue: m.base,
      pointCount: 1,
    })),
  };

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

  private timers: ReturnType<typeof setInterval>[] = [];
  private started = false;

  private ensureStarted(): void {
    if (this.started) {
      return;
    }
    this.started = true;

    // Seed a few traces/logs so the pages aren't empty on first paint.
    for (let i = 0; i < 4; i++) {
      this.tickTelemetry();
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
      resourceName: def.resource,
      unit: def.unit,
      kind: def.kind,
      timestampsMs: ts,
    };

    if (def.kind === "histogram") {
      return { ...base, p50: hist.p50.slice(start), p90: hist.p90.slice(start), p99: hist.p99.slice(start) };
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
      return { kind: "failed", message: `Resource '${args.resourceName}' not found.` };
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
        return { kind: "succeeded", message: "Awaiting input…" };
      default:
        return { kind: "undefined", message: `Unknown command '${args.commandName}'.` };
    }

    return { kind: "succeeded", message: `Command '${args.commandName}' executed on '${args.resourceName}'.` };
  }

  // --- Interactions (mock) ---

  private buildScaleDialog(errorsFor: { name: string; error: string }[]): InteractionInfo {
    const errs = (name: string): string[] => errorsFor.filter((e) => e.name === name).map((e) => e.error);
    const inputs: InteractionInputInfo[] = [
      {
        name: "replicas", label: "Replicas", placeholder: "1-10", inputType: "number", required: true,
        options: [], value: "1", validationErrors: errs("replicas"), description: "Number of instances to run.",
        maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: true,
      },
      {
        name: "tier", label: "Tier", placeholder: "", inputType: "choice", required: true,
        options: [["standard", "Standard"], ["premium", "Premium"]], value: "standard",
        validationErrors: errs("tier"), description: "Compute tier for the replicas.",
        maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
      },
      {
        name: "drain", label: "Drain connections before scaling down", placeholder: "", inputType: "boolean",
        required: false, options: [], value: "true", validationErrors: [], description: "",
        maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
      },
    ];
    return {
      interactionId: 1, kind: "inputsDialog", title: "Scale resource",
      message: "Choose how many replicas to run.", primaryButtonText: "Scale", secondaryButtonText: "Cancel",
      showSecondaryButton: true, showDismiss: true, enableMessageMarkdown: false, intent: "none",
      inputs, linkText: "", linkUrl: "",
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

    if (action === "submit" || action === "update") {
      const errors: { name: string; error: string }[] = [];
      const replicas = Number(values.replicas);
      if (!Number.isInteger(replicas) || replicas < 1 || replicas > 10) {
        errors.push({ name: "replicas", error: "Replicas must be a whole number between 1 and 10." });
      }
      if (errors.length > 0 || action === "update") {
        this.dialog = this.buildScaleDialog(errors);
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
        text: `[${new Date().toISOString()}] ${resourceName}: ${logBodies[i % logBodies.length]}`,
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
            text: `[${new Date().toISOString()}] ${resourceName}: ${body}`,
            isStdErr: isErr,
          },
        ],
      };
      for (const cb of subs) {
        cb(structuredClone(event));
      }
    }
  }

  private tickTelemetry(): void {
    // Append a new log record.
    const isErr = Math.random() < 0.18;
    const resourceName = this.resources[Math.floor(Math.random() * 3)]?.name ?? "frontend";
    const traceId = randomHex(32);
    const spanId = randomHex(16);
    const log: LogRecordSummary = {
      timeUnixNano: nowNano(),
      severity: isErr ? "Error" : "Information",
      severityNumber: isErr ? 17 : 9,
      body: isErr
        ? errorBodies[Math.floor(Math.random() * errorBodies.length)]!
        : logBodies[Math.floor(Math.random() * logBodies.length)]!,
      resourceName,
      traceId,
      spanId,
    };
    this.telemetry.recentLogs = [log, ...this.telemetry.recentLogs].slice(0, 200);
    this.telemetry.logCount += 1;

    // Append a nested trace: a server root span with a few staggered child spans
    // (and one grandchild) so the waterfall shows a realistic call timeline.
    const t0 = Date.now() - 220;
    const at = (offsetMs: number) => String(BigInt(t0 + offsetMs) * 1_000_000n);
    const durNanos = (ms: number) => String(Math.floor(ms * 1_000_000));
    const pick = <T,>(items: T[]) => items[Math.floor(Math.random() * items.length)]!;

    const parentId = randomHex(16);
    const dbChildId = randomHex(16);
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
      { spanId: parentId, parentSpanId: null, name: pick(spanNames), kind: "Server", resource: resourceName, start: 0, dur: 200 },
      { spanId: randomHex(16), parentSpanId: parentId, name: "redis GET", kind: "Client", resource: "cache", start: 12, dur: 18 },
      { spanId: dbChildId, parentSpanId: parentId, name: "products.query", kind: "Client", resource: "apiservice", start: 40, dur: 130, error: isErr },
      { spanId: randomHex(16), parentSpanId: dbChildId, name: "npgsql SELECT", kind: "Client", resource: "catalogdb", start: 55, dur: 95 },
      { spanId: randomHex(16), parentSpanId: parentId, name: "serialize", kind: "Internal", resource: resourceName, start: 178, dur: 18 },
    ];
    const newSpans: SpanSummary[] = segments.map((s) => ({
      traceId,
      spanId: s.spanId,
      parentSpanId: s.parentSpanId,
      name: s.name,
      kind: s.kind,
      resourceName: s.resource,
      startUnixNano: at(s.start),
      durationNanos: durNanos(s.dur),
      statusCode: s.error ? "Error" : "Ok",
    }));
    this.telemetry.recentSpans = [...newSpans, ...this.telemetry.recentSpans].slice(0, 200);
    this.telemetry.spanCount += newSpans.length;

    // Advance each metric's last value with bounded jitter, and append a
    // timestamped sample to its history so the chart shows a real time series.
    const now = Date.now();
    this.telemetry.metrics = this.telemetry.metrics.map((metric, i) => {
      const def = metricDefs[i]!;
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
