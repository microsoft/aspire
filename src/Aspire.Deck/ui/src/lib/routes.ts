export type PageId = "resources" | "parameters" | "console" | "logs" | "traces" | "metrics" | "canvases";

export interface DashboardRoute {
  page: PageId;
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

export function readDashboardRoute(location: Pick<Location, "pathname"> = window.location): DashboardRoute {
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
  url.pathname = pagePath(route.page);
  url.hash = "";
  return `${url.pathname}${url.search}`;
}
