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
  MetricSummary,
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
];

// Two attached AppHosts so the switcher is exercisable in browser/mock mode.
const mockApphosts: AppHostInfo[] = [
  { id: "local", name: "TestShop", state: "connected", active: true },
  { id: "demo-2", name: "OrdersService", state: "connected", active: false },
];

const metricDefs: Array<{ name: string; unit: string | null; resource: string; base: number; jitter: number }> = [
  { name: "http.server.request.duration", unit: "ms", resource: "frontend", base: 42, jitter: 18 },
  { name: "http.server.active_requests", unit: "{request}", resource: "frontend", base: 7, jitter: 6 },
  { name: "http.client.request.duration", unit: "ms", resource: "apiservice", base: 28, jitter: 12 },
  { name: "db.client.connections.usage", unit: "{connection}", resource: "postgres", base: 14, jitter: 4 },
  { name: "process.runtime.dotnet.gc.heap.size", unit: "By", resource: "apiservice", base: 33_554_432, jitter: 4_194_304 },
  { name: "cache.hit_ratio", unit: "1", resource: "cache", base: 0.92, jitter: 0.06 },
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
      lastValue: m.base,
      pointCount: 1,
    })),
  };

  private resourceSubs = new Set<(e: ResourcesEvent) => void>();
  private connectionSubs = new Set<(s: ConnectionStatus) => void>();
  private telemetrySubs = new Set<(t: TelemetrySummary) => void>();
  private apphostSubs = new Set<(a: AppHostInfo[]) => void>();
  private interactionSubs = new Set<(i: InteractionInfo) => void>();
  private pendingInteraction: InteractionInfo | null = null;
  private consoleSubs = new Map<string, Set<(e: ConsoleLogEvent) => void>>();
  private consoleLineCounters = new Map<string, number>();

  private timers: ReturnType<typeof setInterval>[] = [];
  private started = false;

  private ensureStarted(): void {
    if (this.started) {
      return;
    }
    this.started = true;

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
        this.emitInteraction(this.buildScaleDialog([]));
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

  private emitInteraction(i: InteractionInfo): void {
    this.pendingInteraction = i.kind === "complete" ? null : i;
    for (const cb of this.interactionSubs) {
      cb(i);
    }
  }

  onInteraction(cb: (i: InteractionInfo) => void): Unsubscribe {
    this.interactionSubs.add(cb);
    cb(this.pendingInteraction ?? this.completeInteraction());
    return () => this.interactionSubs.delete(cb);
  }

  respondInteraction(action: string, values: Record<string, string>): void {
    if (action === "submit" || action === "update") {
      const errors: { name: string; error: string }[] = [];
      const replicas = Number(values.replicas);
      if (!Number.isInteger(replicas) || replicas < 1 || replicas > 10) {
        errors.push({ name: "replicas", error: "Replicas must be a whole number between 1 and 10." });
      }
      if (errors.length > 0 || action === "update") {
        this.emitInteraction(this.buildScaleDialog(errors));
        return;
      }
      this.emitInteraction(this.completeInteraction());
      return;
    }
    // cancel / dismiss
    this.emitInteraction(this.completeInteraction());
  }

  private completeInteraction(): InteractionInfo {
    return {
      interactionId: 0, kind: "complete", title: "", message: "", primaryButtonText: "", secondaryButtonText: "",
      showSecondaryButton: false, showDismiss: false, enableMessageMarkdown: false, intent: "none",
      inputs: [], linkText: "", linkUrl: "",
    };
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

    // Append a small trace with one parent and one or two children.
    const parentId = randomHex(16);
    const baseStart = nowNano(-50);
    const parent: SpanSummary = {
      traceId,
      spanId: parentId,
      parentSpanId: null,
      name: spanNames[Math.floor(Math.random() * spanNames.length)]!,
      kind: "Server",
      resourceName,
      startUnixNano: baseStart,
      durationNanos: String(Math.floor((20 + Math.random() * 120) * 1_000_000)),
      statusCode: isErr ? "Error" : "Ok",
    };
    const child: SpanSummary = {
      traceId,
      spanId: randomHex(16),
      parentSpanId: parentId,
      name: spanNames[Math.floor(Math.random() * spanNames.length)]!,
      kind: "Client",
      resourceName: this.resources[Math.floor(Math.random() * this.resources.length)]?.name ?? null,
      startUnixNano: nowNano(-30),
      durationNanos: String(Math.floor((5 + Math.random() * 40) * 1_000_000)),
      statusCode: "Ok",
    };
    this.telemetry.recentSpans = [parent, child, ...this.telemetry.recentSpans].slice(0, 200);
    this.telemetry.spanCount += 2;

    // Advance each metric's last value with bounded jitter.
    this.telemetry.metrics = this.telemetry.metrics.map((metric, i) => {
      const def = metricDefs[i]!;
      const prev = metric.lastValue ?? def.base;
      let next = prev + (Math.random() - 0.5) * def.jitter;
      // Keep ratios within [0, 1].
      if (def.unit === "1") {
        next = Math.max(0, Math.min(1, next));
      } else {
        next = Math.max(0, next);
      }
      return { ...metric, lastValue: next, pointCount: metric.pointCount + 1 };
    });

    const snapshot = this.getTelemetrySummary();
    for (const cb of this.telemetrySubs) {
      cb(snapshot);
    }
  }
}

export const mockBackend = new MockBackend();
