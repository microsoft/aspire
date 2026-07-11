export type PageId = "resources" | "parameters" | "console" | "logs" | "traces" | "metrics" | "canvases";

export interface DashboardRoute {
  page: PageId;
  traceId?: string;
  spanId?: string;
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
    return { page: "console" };
  }
  if (path === "/structuredlogs" || path.startsWith("/structuredlogs/")) {
    return { page: "logs" };
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
    : pagePath(route.page);
  url.searchParams.delete("span");
  if (route.spanId) {
    url.searchParams.set("span", route.spanId);
  }
  url.hash = "";
  return `${url.pathname}${url.search}`;
}
