export const dashboardCoreFeatures = {
  "APP-BROWSER-001": "The dashboard loads without browser, page, or network errors.",
  "APP-SHELL-001": "The shell identifies the app, version, navigation, and initial page.",
  "APP-NAV-001": "Every dashboard page is reachable from navigation and reports the active page.",
  "APP-PAGE-001": "Every dashboard route uses a named page region, heading, body, and named toolbar where tools are available.",
  "APP-CONNECTION-001": "Resource service and OTLP connection states become connected.",
  "APP-APPHOST-001": "The AppHost switcher lists attached hosts and changes the active host.",
  "APP-THEME-001": "The dashboard switches and persists light and dark themes.",
  "APP-NOTIFICATION-001": "Notifications expose primary, secondary, and dismiss actions.",
  "APP-RESPONSIVE-001": "The shell, navigation, resource table, and drawer remain usable on mobile.",
  "RES-LIST-001": "Resources exclude hidden and parameter resources and retain stable ordering.",
  "RES-FILTER-001": "Resources filter by name, type, and state and expose an empty result.",
  "RES-ENDPOINT-001": "External resource endpoints retain actionable URLs without selecting the row.",
  "RES-DETAILS-001": "Resource details include overview, endpoints, properties, environment, health, and relationships.",
  "RES-SECRETS-001": "Every environment value is masked until the user explicitly reveals it.",
  "RES-COMMANDS-001": "Resource commands expose enabled state and update the live resource state.",
  "RES-CONFIRM-001": "Destructive resource commands require confirmation and report their result.",
  "RES-INTERACTION-001": "Input commands support text, choice, boolean, live validation, and submission.",
  "PARAM-LIST-001": "Parameters distinguish plain, secret, and unresolved values.",
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
