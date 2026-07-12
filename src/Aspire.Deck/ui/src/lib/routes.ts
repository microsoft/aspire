export type PageId = "resources" | "parameters" | "console" | "logs" | "traces" | "metrics" | "canvases" | "notFound" | "error";

export interface DashboardRoute {
  page: PageId;
  traceId?: string;
  spanId?: string;
  consoleResourceName?: string;
  consoleShowTimestamps?: boolean;
  consoleTimestampsUtc?: boolean;
  consoleWrapLines?: boolean;
  consolePaused?: boolean;
  traceResourceName?: string;
  traceType?: string;
  traceQuery?: string;
  traceMinDurationMs?: number;
  tracePaused?: boolean;
  traceFilters?: string;
  logResourceName?: string;
  logQuery?: string;
  logSeverity?: string;
  logPaused?: boolean;
  logFilters?: string;
  metricResourceName?: string;
  metricMeterName?: string;
  metricName?: string;
  metricWindowSeconds?: number;
  metricView?: "chart" | "table";
  metricsPaused?: boolean;
  metricDimensions?: Record<string, Array<string | null>>;
  metricHistogramCount?: boolean;
  resourceName?: string;
  resourceQuery?: string;
  resourceHiddenTypes?: string[];
  resourceHiddenStates?: string[];
  resourceHiddenHealth?: string[];
  resourceShowHidden?: boolean;
  resourceShowType?: boolean;
  resourceCollapsed?: string[];
  resourceSortColumn?: string;
  resourceSortDirection?: "ascending" | "descending";
  resourceView?: "table" | "graph";
  parameterName?: string;
  parameterQuery?: string;
  parameterSortColumn?: string;
  parameterSortDirection?: "ascending" | "descending";
}

const pagePaths: Record<PageId, string> = {
  resources: "/",
  parameters: "/parameters",
  console: "/consolelogs",
  logs: "/structuredlogs",
  traces: "/traces",
  metrics: "/metrics",
  canvases: "/canvases",
  notFound: "/error/404",
  error: "/error",
};

export function pagePath(page: PageId): string {
  return pagePaths[page];
}

function decodeRoutePart(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

export function readDashboardRoute(
  location: Pick<Location, "pathname" | "search"> = window.location,
): DashboardRoute {
  const path = location.pathname.toLowerCase();
  if (path === "/parameters" || path.startsWith("/parameters/")) {
    const search = new URLSearchParams(location.search);
    return {
      page: "parameters",
      parameterName: search.get("resource") || undefined,
      parameterQuery: search.get("q") || undefined,
      parameterSortColumn: search.get("sort") || undefined,
      parameterSortDirection: search.get("sortDirection") === "descending" ? "descending" : undefined,
    };
  }
  if (path === "/consolelogs" || path.startsWith("/consolelogs/")) {
    const resourceMatch = /^\/consolelogs\/resource\/([^/]+)\/?$/i.exec(location.pathname);
    const search = new URLSearchParams(location.search);
    return {
      page: "console",
      consoleResourceName: resourceMatch ? decodeRoutePart(resourceMatch[1]!) : undefined,
      consoleShowTimestamps: search.get("timestamps") === "true" || undefined,
      consoleTimestampsUtc: search.get("utc") === "true" || undefined,
      consoleWrapLines: search.get("wrap") === "true" || undefined,
      consolePaused: search.get("paused") === "true" || undefined,
    };
  }
  if (path === "/structuredlogs" || path.startsWith("/structuredlogs/")) {
    const resourceMatch = /^\/structuredlogs\/resource\/([^/]+)\/?$/i.exec(location.pathname);
    const search = new URLSearchParams(location.search);
    return {
      page: "logs",
      spanId: search.get("span") || undefined,
      logResourceName: resourceMatch ? decodeRoutePart(resourceMatch[1]!) : undefined,
      logQuery: search.get("q") || undefined,
      logSeverity: search.get("severity") || undefined,
      logPaused: search.get("paused") === "true" || undefined,
      logFilters: search.get("filters") || undefined,
    };
  }
  if (path === "/traces" || path.startsWith("/traces/")) {
    const search = new URLSearchParams(location.search);
    const minDuration = Number(search.get("minDuration"));
    const traceState = {
      traceResourceName: search.get("resource") || undefined,
      traceType: search.get("type") || undefined,
      traceQuery: search.get("q") || undefined,
      traceMinDurationMs: Number.isFinite(minDuration) && minDuration > 0 ? minDuration : undefined,
      tracePaused: search.get("paused") === "true" || undefined,
      traceFilters: search.get("filters") || undefined,
    };
    const detailMatch = /^\/traces\/detail\/([^/]+)\/?$/i.exec(location.pathname);
    if (detailMatch) {
      const spanId = search.get("span") || undefined;
      return {
        page: "traces",
        traceId: decodeRoutePart(detailMatch[1]!),
        spanId,
        ...traceState,
      };
    }
    return { page: "traces", ...traceState };
  }
  if (path === "/metrics" || path.startsWith("/metrics/")) {
    const instrumentMatch = /^\/metrics\/resource\/([^/]+)\/meter\/([^/]+)\/instrument\/([^/]+)\/?$/i.exec(location.pathname);
    const meterMatch = /^\/metrics\/resource\/([^/]+)\/meter\/([^/]+)\/?$/i.exec(location.pathname);
    const resourceMatch = /^\/metrics\/resource\/([^/]+)\/?$/i.exec(location.pathname);
    const search = new URLSearchParams(location.search);
    const durationMinutes = Number(search.get("duration"));
    const rangeSeconds = Number(search.get("range"));
    const windowSeconds = Number.isFinite(durationMinutes) && durationMinutes > 0
      ? durationMinutes * 60
      : rangeSeconds;
    const view = search.get("view");
    const dimensions: Record<string, Array<string | null>> = {};
    for (const encoded of search.getAll("dimension")) {
      try {
        const parsed = JSON.parse(encoded) as unknown;
        if (Array.isArray(parsed) && typeof parsed[0] === "string" && Array.isArray(parsed[1])) {
          dimensions[parsed[0]] = parsed[1].filter((value): value is string | null => value === null || typeof value === "string");
        }
      } catch {
        // Ignore malformed state from hand-edited URLs and restore the default selection.
      }
    }
    return {
      page: "metrics",
      metricResourceName: instrumentMatch || meterMatch
        ? decodeRoutePart((instrumentMatch ?? meterMatch)![1]!)
        : resourceMatch ? decodeRoutePart(resourceMatch[1]!) : undefined,
      metricMeterName: instrumentMatch || meterMatch
        ? decodeRoutePart((instrumentMatch ?? meterMatch)![2]!)
        : undefined,
      metricName: instrumentMatch ? decodeRoutePart(instrumentMatch[3]!) : search.get("metric") || undefined,
      metricWindowSeconds: Number.isFinite(windowSeconds) && windowSeconds > 0 ? windowSeconds : undefined,
      metricView: view?.toLowerCase() === "table" ? "table" : undefined,
      metricsPaused: search.get("paused") === "true" || undefined,
      metricDimensions: Object.keys(dimensions).length > 0 ? dimensions : undefined,
      metricHistogramCount: search.get("histogram") === "count" || undefined,
    };
  }
  if (path === "/canvases" || path.startsWith("/canvases/")) {
    return { page: "canvases" };
  }
  if (path === "/error") {
    return { page: "error" };
  }
  if (path === "/" || path === "") {
    const search = new URLSearchParams(location.search);
    return {
      page: "resources",
      resourceName: search.get("resource") || undefined,
      resourceQuery: search.get("q") || undefined,
      resourceHiddenTypes: search.getAll("hiddenType"),
      resourceHiddenStates: search.getAll("hiddenState"),
      resourceHiddenHealth: search.getAll("hiddenHealth"),
      resourceShowHidden: search.get("showHiddenResources") === "true" || undefined,
      resourceShowType: search.get("showResourceTypes") === "true" || undefined,
      resourceCollapsed: search.getAll("collapsed"),
      resourceSortColumn: search.get("sort") || undefined,
      resourceSortDirection: search.get("sortDirection") === "descending" ? "descending" : undefined,
      resourceView: search.get("view")?.toLowerCase() === "graph" ? "graph" : undefined,
    };
  }
  return { page: "notFound" };
}

export function dashboardRouteHref(route: DashboardRoute, location: Location = window.location): string {
  const url = new URL(location.href);
  url.pathname = route.page === "traces" && route.traceId
    ? `/traces/detail/${encodeURIComponent(route.traceId)}`
    : route.page === "logs" && route.logResourceName
      ? `/structuredlogs/resource/${encodeURIComponent(route.logResourceName)}`
    : route.page === "console" && route.consoleResourceName
      ? `/consolelogs/resource/${encodeURIComponent(route.consoleResourceName)}`
      : route.page === "metrics" && route.metricResourceName && route.metricMeterName && route.metricName
        ? `/metrics/resource/${encodeURIComponent(route.metricResourceName)}/meter/${encodeURIComponent(route.metricMeterName)}/instrument/${encodeURIComponent(route.metricName)}`
        : route.page === "metrics" && route.metricResourceName && route.metricMeterName
          ? `/metrics/resource/${encodeURIComponent(route.metricResourceName)}/meter/${encodeURIComponent(route.metricMeterName)}`
          : route.page === "metrics" && route.metricResourceName
            ? `/metrics/resource/${encodeURIComponent(route.metricResourceName)}`
        : pagePath(route.page);
  url.searchParams.delete("span");
  url.searchParams.delete("timestamps");
  url.searchParams.delete("utc");
  url.searchParams.delete("wrap");
  url.searchParams.delete("paused");
  url.searchParams.delete("resource");
  url.searchParams.delete("type");
  url.searchParams.delete("q");
  url.searchParams.delete("minDuration");
  url.searchParams.delete("metric");
  url.searchParams.delete("range");
  url.searchParams.delete("duration");
  url.searchParams.delete("view");
  url.searchParams.delete("dimension");
  url.searchParams.delete("histogram");
  url.searchParams.delete("hiddenType");
  url.searchParams.delete("hiddenState");
  url.searchParams.delete("hiddenHealth");
  url.searchParams.delete("showHiddenResources");
  url.searchParams.delete("showResourceTypes");
  url.searchParams.delete("collapsed");
  url.searchParams.delete("sort");
  url.searchParams.delete("sortDirection");
  url.searchParams.delete("severity");
  url.searchParams.delete("filters");
  if (route.spanId) {
    url.searchParams.set("span", route.spanId);
  }
  if (route.page === "console") {
    if (route.consoleShowTimestamps) {
      url.searchParams.set("timestamps", "true");
    }
    if (route.consoleTimestampsUtc) {
      url.searchParams.set("utc", "true");
    }
    if (route.consoleWrapLines) {
      url.searchParams.set("wrap", "true");
    }
    if (route.consolePaused) {
      url.searchParams.set("paused", "true");
    }
  }
  if (route.page === "traces") {
    if (route.traceResourceName) {
      url.searchParams.set("resource", route.traceResourceName);
    }
    if (route.traceType && route.traceType !== "all") {
      url.searchParams.set("type", route.traceType);
    }
    if (route.traceQuery) {
      url.searchParams.set("q", route.traceQuery);
    }
    if (route.traceMinDurationMs) {
      url.searchParams.set("minDuration", route.traceMinDurationMs.toString());
    }
    if (route.tracePaused) {
      url.searchParams.set("paused", "true");
    }
    if (route.traceFilters) {
      url.searchParams.set("filters", route.traceFilters);
    }
  }
  if (route.page === "logs") {
    if (route.logQuery) url.searchParams.set("q", route.logQuery);
    if (route.logSeverity && route.logSeverity !== "All") url.searchParams.set("severity", route.logSeverity);
    if (route.logPaused) url.searchParams.set("paused", "true");
    if (route.logFilters) url.searchParams.set("filters", route.logFilters);
  }
  if (route.page === "metrics") {
    if (route.metricWindowSeconds && route.metricWindowSeconds !== 300) {
      url.searchParams.set("duration", (route.metricWindowSeconds / 60).toString());
    }
    if (route.metricView === "table") {
      url.searchParams.set("view", "Table");
    }
    if (route.metricsPaused) {
      url.searchParams.set("paused", "true");
    }
    for (const [name, values] of Object.entries(route.metricDimensions ?? {}).sort(([left], [right]) => left.localeCompare(right))) {
      url.searchParams.append("dimension", JSON.stringify([name, values]));
    }
    if (route.metricHistogramCount) {
      url.searchParams.set("histogram", "count");
    }
  }
  if (route.page === "resources") {
    if (route.resourceName) url.searchParams.set("resource", route.resourceName);
    if (route.resourceQuery) url.searchParams.set("q", route.resourceQuery);
    for (const value of route.resourceHiddenTypes ?? []) url.searchParams.append("hiddenType", value);
    for (const value of route.resourceHiddenStates ?? []) url.searchParams.append("hiddenState", value);
    for (const value of route.resourceHiddenHealth ?? []) url.searchParams.append("hiddenHealth", value);
    if (route.resourceShowHidden) url.searchParams.set("showHiddenResources", "true");
    if (route.resourceShowType) url.searchParams.set("showResourceTypes", "true");
    for (const value of route.resourceCollapsed ?? []) url.searchParams.append("collapsed", value);
    if (route.resourceSortColumn && route.resourceSortColumn !== "name") url.searchParams.set("sort", route.resourceSortColumn);
    if (route.resourceSortDirection === "descending") url.searchParams.set("sortDirection", "descending");
    if (route.resourceView === "graph") url.searchParams.set("view", "Graph");
  }
  if (route.page === "parameters") {
    if (route.parameterName) url.searchParams.set("resource", route.parameterName);
    if (route.parameterQuery) url.searchParams.set("q", route.parameterQuery);
    if (route.parameterSortColumn && route.parameterSortColumn !== "name") url.searchParams.set("sort", route.parameterSortColumn);
    if (route.parameterSortDirection === "descending") url.searchParams.set("sortDirection", "descending");
  }
  url.hash = "";
  return `${url.pathname}${url.search}`;
}
