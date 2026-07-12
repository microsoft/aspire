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
  metricResourceName?: string;
  metricMeterName?: string;
  metricName?: string;
  metricWindowSeconds?: number;
  metricView?: "chart" | "table";
  metricsPaused?: boolean;
  metricDimensions?: Record<string, Array<string | null>>;
  metricHistogramCount?: boolean;
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
    return { page: "parameters" };
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
    const spanId = new URLSearchParams(location.search).get("span") || undefined;
    return { page: "logs", spanId };
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
    return { page: "resources" };
  }
  return { page: "notFound" };
}

export function dashboardRouteHref(route: DashboardRoute, location: Location = window.location): string {
  const url = new URL(location.href);
  url.pathname = route.page === "traces" && route.traceId
    ? `/traces/detail/${encodeURIComponent(route.traceId)}`
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
  url.hash = "";
  return `${url.pathname}${url.search}`;
}
