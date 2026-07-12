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
    const detailMatch = /^\/traces\/detail\/([^/]+)\/?$/i.exec(location.pathname);
    if (detailMatch) {
      const spanId = new URLSearchParams(location.search).get("span") || undefined;
      return {
        page: "traces",
        traceId: decodeRoutePart(detailMatch[1]!),
        spanId,
      };
    }
    return { page: "traces" };
  }
  if (path === "/metrics" || path.startsWith("/metrics/")) {
    return { page: "metrics" };
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
      : pagePath(route.page);
  url.searchParams.delete("span");
  url.searchParams.delete("timestamps");
  url.searchParams.delete("utc");
  url.searchParams.delete("wrap");
  url.searchParams.delete("paused");
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
  url.hash = "";
  return `${url.pathname}${url.search}`;
}
