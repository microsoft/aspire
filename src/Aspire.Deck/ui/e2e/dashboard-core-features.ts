export const dashboardCoreFeatures = {
  "APP-BROWSER-001": "The dashboard loads without browser, page, or network errors.",
  "APP-SHELL-001": "The shell identifies the app, version, navigation, and initial page.",
  "APP-NAV-001": "Every dashboard page is reachable from navigation and reports the active page.",
  "APP-ROUTES-001": "Dashboard pages have stable URLs that restore on reload and support browser back and forward navigation.",
  "APP-PAGE-001": "Every dashboard route uses a named page region, heading, body, and named toolbar where tools are available.",
  "APP-CONNECTION-001": "Resource service and OTLP connection states become connected.",
  "APP-APPHOST-001": "The AppHost switcher lists attached hosts and changes the active host.",
  "APP-THEME-001": "The dashboard selects and persists system, light, and dark themes.",
  "APP-REPOSITORY-001": "The top bar links to the Aspire repository in a safe external tab.",
  "APP-HELP-001": "Help exposes dashboard documentation and every implemented keyboard shortcut.",
  "APP-SETTINGS-001": "Settings reports dashboard/runtime versions and controls theme selection.",
  "APP-KEYBOARD-001": "Page, Help, and Settings shortcuts work outside editable controls.",
  "APP-NOTFOUND-001": "Unknown paths render a dedicated 404 experience and recover to Resources.",
  "APP-ERROR-001": "The error route exposes recovery and reload actions.",
  "APP-NOTIFICATION-001": "Notifications expose primary, secondary, and dismiss actions.",
  "APP-RESPONSIVE-001": "The shell, navigation, resource table, and drawer remain usable on mobile.",
  "RES-LIST-001": "Resources exclude hidden and parameter resources and retain stable ordering.",
  "RES-SORT-001": "Resources sort by supported columns in ascending and descending order.",
  "RES-FILTER-001": "Resources filter by name, type, and state and expose an empty result.",
  "RES-STRUCTURED-FILTER-001": "Resource state, type, and health filters compose, clear, and survive reload.",
  "RES-VIEW-OPTIONS-001": "Resource view options control hidden resources, type visibility, and hierarchy expansion.",
  "RES-HIERARCHY-001": "Parent and child resources render as an expandable hierarchy.",
  "RES-SOURCE-001": "Project, executable, container, and custom sources render from the resource property contract.",
  "RES-DETAILS-LINK-001": "Resource detail selection is deep-linkable and restorable.",
  "RES-GRAPH-001": "Resources and relationships render in a route-restorable graph view.",
  "RES-GRAPH-ZOOM-001": "The resource graph supports zoom in, zoom out, reset, pan, and drag.",
  "RES-GRAPH-CONTEXT-001": "Graph nodes expose details and resource commands through a context menu.",
  "RES-ENDPOINT-001": "External resource endpoints retain actionable URLs without selecting the row.",
  "RES-ICON-001": "Resource and command icons honor Fluent names and regular/filled variants.",
  "RES-DETAILS-001": "Resource details include overview, endpoints, properties, environment, health, and relationships.",
  "RES-SECRETS-001": "Every environment value is masked until the user explicitly reveals it.",
  "RES-COMMANDS-001": "Resource commands expose enabled state and update the live resource state.",
  "RES-ACTION-MENU-001": "Highlighted resource commands stay directly available while remaining commands use the shared overflow menu.",
  "RES-CONFIRM-001": "Destructive resource commands require confirmation and report their result.",
  "RES-INTERACTION-001": "Input commands support text, choice, boolean, live validation, and submission.",
  "PARAM-LIST-001": "Parameters distinguish plain, secret, and unresolved values.",
  "PARAM-SORT-001": "Parameters sort by supported columns in ascending and descending order.",
  "PARAM-FILTER-001": "Parameters filter by name and state.",
  "PARAM-SECRET-001": "Secret parameter values reveal without selecting the resource row.",
} as const;

export type DashboardCoreFeatureId = keyof typeof dashboardCoreFeatures;

export function getMissingDashboardCoreFeatures(
  covered: ReadonlySet<DashboardCoreFeatureId>,
): DashboardCoreFeatureId[] {
  return (Object.keys(dashboardCoreFeatures) as DashboardCoreFeatureId[]).filter(
    (feature) => !covered.has(feature),
  );
}
