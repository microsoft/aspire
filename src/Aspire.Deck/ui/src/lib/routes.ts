export type PageId = "resources" | "parameters" | "console" | "logs" | "traces" | "metrics" | "canvases";

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
  metricName?: string;
  metricWindowSeconds?: number;
  metricView?: "chart" | "table";
  metricsPaused?: boolean;
}

const pagePaths: Record<PageId, string> = {
  resources: "/",
  parameters: "/parameters",
  console: "/consolelogs",
  logs: "/structuredlogs",
  traces: "/traces",
  metrics: "/metrics",
  canvases: "/canvases",
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
    const resourceMatch = /^\/metrics\/resource\/([^/]+)\/?$/i.exec(location.pathname);
    const search = new URLSearchParams(location.search);
    const windowSeconds = Number(search.get("range"));
    const view = search.get("view");
    return {
      page: "metrics",
      metricResourceName: resourceMatch ? decodeRoutePart(resourceMatch[1]!) : undefined,
      metricName: search.get("metric") || undefined,
      metricWindowSeconds: Number.isFinite(windowSeconds) && windowSeconds > 0 ? windowSeconds : undefined,
      metricView: view === "table" ? "table" : undefined,
      metricsPaused: search.get("paused") === "true" || undefined,
    };
  }
  if (path === "/canvases" || path.startsWith("/canvases/")) {
    return { page: "canvases" };
  }
  return { page: "resources" };
}

export function dashboardRouteHref(route: DashboardRoute, location: Location = window.location): string {
  const url = new URL(location.href);
  url.pathname = route.page === "traces" && route.traceId
    ? `/traces/detail/${encodeURIComponent(route.traceId)}`
    : route.page === "console" && route.consoleResourceName
      ? `/consolelogs/resource/${encodeURIComponent(route.consoleResourceName)}`
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
  url.searchParams.delete("view");
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
    if (route.metricName) {
      url.searchParams.set("metric", route.metricName);
    }
    if (route.metricWindowSeconds && route.metricWindowSeconds !== 300) {
      url.searchParams.set("range", route.metricWindowSeconds.toString());
    }
    if (route.metricView === "table") {
      url.searchParams.set("view", "table");
    }
    if (route.metricsPaused) {
      url.searchParams.set("paused", "true");
    }
  }
  url.hash = "";
  return `${url.pathname}${url.search}`;
}
