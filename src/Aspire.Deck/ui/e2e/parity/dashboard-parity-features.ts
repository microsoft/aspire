export type DashboardArea =
  | "shell"
  | "resources"
  | "parameters"
  | "commands"
  | "console"
  | "structured-logs"
  | "traces"
  | "metrics";

export type ReactParityStatus = "covered" | "partial" | "missing";

export type LegacyScenario =
  | "shell"
  | "resources"
  | "parameters"
  | "commands"
  | "console"
  | "structured-logs"
  | "traces"
  | "metrics";

export interface DashboardParityFeature {
  id: string;
  area: DashboardArea;
  legacyRoute: string;
  description: string;
  legacyScenario: LegacyScenario | null;
  reactStatus: ReactParityStatus;
  currentCoverage: string | null;
}

type FeatureDefinition = readonly [
  id: string,
  legacyRoute: string,
  description: string,
  legacyScenario: LegacyScenario | null,
  reactStatus: ReactParityStatus,
  currentCoverage?: string,
];

// This is the migration ledger, not a list of what the React prototype happens to implement.
// Entries come from the legacy dashboard's visible behavior and the Stress AppHost scenarios.
// A null legacyScenario is an explicit test gap that must be replaced by a black-box scenario.
const featureDefinitions = {
  shell: [
    ["SHELL-IDENTITY-001", "/", "Application identity and dashboard version are visible.", "shell", "covered", "APP-SHELL-001; STRESS-CONFIG-001"],
    ["SHELL-NAV-001", "/", "Resources, Parameters, Graph, Console, Structured Logs, Traces, and Metrics are reachable from navigation.", "shell", "covered", "APP-NAV-001; RES-GRAPH-001"],
    ["SHELL-ROUTES-001", "/", "Pages have stable URLs and browser history/deep links restore the selected page.", "shell", "covered", "APP-ROUTES-001"],
    ["SHELL-REPO-001", "/", "The Aspire repository link is available from the top bar.", "shell", "covered", "APP-REPOSITORY-001"],
    ["SHELL-HELP-001", "/", "Help opens with documentation and keyboard shortcut reference content.", "shell", "covered", "APP-HELP-001"],
    ["SHELL-KEYBOARD-001", "/", "Page navigation, panel, help, and settings keyboard shortcuts work.", "shell", "covered", "APP-KEYBOARD-001"],
    ["SHELL-AGENTS-001", "/", "The AI agents entry point appears when enabled.", "shell", "covered", "HTTP-AI-AGENTS-001; DeckApiTests.GetConfig_ReturnsDeckConfigContract"],
    ["SHELL-ASSISTANT-001", "/", "The AI assistant opens, closes, expands, starts a new chat, and sends or stops responses.", null, "covered", "APP-ASSISTANT-001; HTTP-ASSISTANT-001; DeckApiTests.AssistantEndpoints_ReturnNotFoundWhenAssistantIsDisabled"],
    ["SHELL-NOTIFICATIONS-001", "/", "Active notifications render intent, actions, links, and dismiss behavior.", "shell", "covered", "APP-NOTIFICATION-001; CMD-NOTIFICATION-001"],
    ["SHELL-NOTIFICATION-CENTER-001", "/", "The notification center opens and preserves notification history.", "shell", "covered", "APP-NOTIFICATION-CENTER-001"],
    ["SHELL-SETTINGS-001", "/", "Settings opens from the top bar and reports dashboard/runtime versions.", "shell", "covered", "APP-SETTINGS-001; DeckApiTests.GetConfig_ReturnsDeckConfigContract"],
    ["SHELL-THEME-001", "/", "System, light, and dark theme selection is persisted.", "shell", "covered", "APP-THEME-001; APP-SETTINGS-001"],
    ["SHELL-LANGUAGE-001", "/", "The dashboard language can be selected and applied.", "shell", "covered", "HTTP-LANGUAGE-001; GlobalizationHelpersTests.ResolveSetCultureToAcceptedCultureAsync_MatchRequestToResult"],
    ["SHELL-TIME-FORMAT-001", "/", "System, 12-hour, and 24-hour time formatting can be selected.", "shell", "covered", "APP-TIME-FORMAT-001"],
    ["SHELL-MANAGE-DATA-001", "/", "Resource logs and telemetry can be inspected, exported, imported, and cleared.", null, "covered", "HTTP-MANAGE-DATA-001; DeckApiTests.ManageData_InventoryExportImportAndRemoveUseDeckContract"],
    ["SHELL-USER-001", "/", "Authenticated user profile and sign-out behavior are available when configured.", null, "covered", "HTTP-USER-001; DeckApiTests.GetConfig_ReturnsDeckConfigContract"],
    ["SHELL-AUTH-001", "/login", "Browser-token and OpenID Connect login flows protect the frontend.", null, "covered", "HTTP-AUTH-001; DeckApiTests.GetResources_BrowserTokenAuthWithoutCookie_RedirectsToLogin; BrowserTokenAuthenticationTests; FrontendOpenIdConnectAuthTests"],
    ["SHELL-RECONNECT-001", "/", "A lost dashboard circuit or backend connection exposes reconnect and recovery UI.", null, "covered", "HTTP-RECOVERY-001; HTTP-RECONNECT-001"],
    ["SHELL-UNSECURED-001", "/", "An unsecured telemetry/API endpoint warning is visible with supporting guidance.", null, "covered", "HTTP-SHELL-UNSECURED-001; DeckApiTests.GetConfig_ReturnsDeckConfigContract"],
    ["SHELL-NOTFOUND-001", "/error/404", "Unknown routes render a dedicated not-found experience.", "shell", "covered", "APP-NOTFOUND-001"],
    ["SHELL-ERROR-001", "/error", "Unhandled errors render a recoverable error experience.", "shell", "covered", "APP-ERROR-001"],
    ["SHELL-RESPONSIVE-001", "/", "Navigation, header actions, pages, and overlays remain usable on mobile.", "shell", "covered", "APP-RESPONSIVE-001; OBS-RESPONSIVE-001; STRESS-RESPONSIVE-001"],
    ["SHELL-ACCESSIBILITY-001", "/", "Landmarks, names, focus order, dialogs, and keyboard interaction remain accessible.", "shell", "covered", "APP-PAGE-001; APP-ACCESSIBILITY-001; toolkit.aria.yml"],
    ["SHELL-BROWSER-ERRORS-001", "/", "Normal navigation and interaction produce no page or console errors.", "shell", "covered", "APP-BROWSER-001"],
  ],
  resources: [
    ["RES-LIST-001", "/", "The live resource inventory renders and excludes parameter resources.", "resources", "covered", "RES-LIST-001; STRESS-RESOURCES-001"],
    ["RES-HIDDEN-001", "/", "Hidden resources are excluded by default and can be shown through view options.", "resources", "covered", "RES-VIEW-OPTIONS-001; STRESS-VISIBILITY-001"],
    ["RES-COUNT-001", "/", "The visible resource count tracks filtering and view options.", "resources", "covered", "STRESS-RESOURCES-001"],
    ["RES-TEXT-FILTER-001", "/", "Resources filter by text and expose an empty result state.", "resources", "covered", "RES-FILTER-001"],
    ["RES-STRUCTURED-FILTER-001", "/", "Resource state, type, and health filters can be composed and cleared.", "resources", "covered", "RES-STRUCTURED-FILTER-001"],
    ["RES-VIEW-OPTIONS-001", "/", "View options control hidden resources, resource type, and hierarchy expansion.", "resources", "covered", "RES-VIEW-OPTIONS-001"],
    ["RES-SORT-001", "/", "Supported resource columns sort ascending and descending.", "resources", "covered", "RES-SORT-001"],
    ["RES-COLUMNS-001", "/", "Name, state, source, URLs, and start time columns render the legacy data model.", "resources", "covered", "RES-LIST-001; RES-ENDPOINT-001; RES-SOURCE-001"],
    ["RES-SOURCE-001", "/", "Project, executable, container, and custom resource sources are displayed.", "resources", "covered", "RES-SOURCE-001"],
    ["RES-URLS-001", "/", "External endpoints use display names and preserve internal/inactive endpoint rules.", "resources", "covered", "RES-ENDPOINT-001"],
    ["RES-LONG-URLS-001", "/", "Large endpoint sets and very long URLs remain usable without breaking layout.", "resources", "covered", "RES-LONG-URLS-001; APP-RESPONSIVE-001"],
    ["RES-NESTING-001", "/", "Parent/child resources render as a hierarchical tree.", "resources", "covered", "RES-HIERARCHY-001"],
    ["RES-EXPAND-001", "/", "Individual branches and all branches can be expanded and collapsed.", "resources", "covered", "RES-HIERARCHY-001; RES-VIEW-OPTIONS-001"],
    ["RES-GRAPH-001", "/?view=Graph", "Resources and relationships render in the graph view.", "resources", "covered", "RES-GRAPH-001"],
    ["RES-GRAPH-ZOOM-001", "/?view=Graph", "Graph zoom in, zoom out, and reset controls work.", "resources", "covered", "RES-GRAPH-ZOOM-001"],
    ["RES-GRAPH-CONTEXT-001", "/?view=Graph", "Graph nodes expose resource actions and details context menus.", "resources", "covered", "RES-GRAPH-CONTEXT-001"],
    ["RES-VIRTUALIZATION-001", "/", "Large resource inventories remain responsive through row virtualization.", null, "covered", "HTTP-RESOURCE-VIRTUALIZATION-001"],
    ["RES-DETAILS-001", "/", "Selecting a resource opens overview, endpoints, properties, environment, health, and relationships.", "resources", "covered", "RES-DETAILS-001; STRESS-DETAILS-001"],
    ["RES-DETAILS-LINK-001", "/?resource={name}", "A resource details selection is deep-linkable and restorable.", "resources", "covered", "RES-DETAILS-LINK-001"],
    ["RES-PROPERTIES-001", "/", "Known, custom, highlighted, null, array, and object properties render correctly.", "resources", "covered", "RES-PROPERTIES-001; RES-DETAILS-001"],
    ["RES-SECRETS-001", "/", "Sensitive properties and environment values remain masked until explicitly revealed.", "resources", "covered", "RES-SECRETS-001; STRESS-SECRETS-001"],
    ["RES-COPY-001", "/", "Resource property and environment values can be copied without accidental disclosure.", "resources", "covered", "RES-COPY-001"],
    ["RES-HEALTH-001", "/", "Health summaries and individual health reports preserve status and descriptions.", "resources", "covered", "RES-DETAILS-001"],
    ["RES-RELATIONSHIPS-001", "/", "Parent, child, wait, reference, and other relationships are visible.", "resources", "covered", "RES-DETAILS-001"],
    ["RES-STATE-001", "/", "Running, starting, finished, exited, not-started, and unknown states remain distinguishable.", "resources", "covered", "HTTP-RESOURCES-001; STRESS-RESOURCES-001"],
    ["RES-NO-STATUS-001", "/", "Resources without status data render a stable unknown state.", "resources", "covered", "RES-NO-STATUS-001; STRESS-RESOURCES-001"],
    ["RES-RESOURCE-ICON-001", "/", "Custom Fluent resource icon names override resource-type fallbacks.", "resources", "covered", "RES-ICON-001; HTTP-RESOURCES-001; STRESS-RESOURCE-ICON-001"],
    ["RES-RESOURCE-ICON-VARIANT-001", "/", "Regular and filled resource icon variants are preserved.", null, "covered", "RES-ICON-001; DeckApiTests.GetResources_ReturnsDeckResourceContract"],
    ["RES-CONTEXT-MENU-001", "/", "Resource rows expose details, navigation, and commands through a context menu.", "resources", "covered", "RES-CONTEXT-MENU-001"],
    ["RES-SESSION-001", "/", "Search, filters, sort, view, expansion, and selection survive navigation and reload.", "resources", "covered", "RES-SORT-001; RES-STRUCTURED-FILTER-001; RES-VIEW-OPTIONS-001; RES-HIERARCHY-001; RES-DETAILS-LINK-001"],
  ],
  parameters: [
    ["PARAM-LIST-001", "/parameters", "Plain, secret, and unresolved parameters render on a dedicated page.", "parameters", "covered", "PARAM-LIST-001; STRESS-PARAMETERS-001"],
    ["PARAM-COUNT-001", "/parameters", "The parameter count tracks the current filter.", "parameters", "covered", "STRESS-PARAMETERS-001"],
    ["PARAM-FILTER-001", "/parameters", "Parameters filter by name and state.", "parameters", "covered", "PARAM-FILTER-001"],
    ["PARAM-SORT-001", "/parameters", "Supported parameter columns sort ascending and descending.", "parameters", "covered", "PARAM-SORT-001"],
    ["PARAM-MISSING-001", "/parameters", "Unresolved parameters expose the value-missing state and placeholder.", "parameters", "covered", "PARAM-LIST-001; STRESS-PARAMETERS-001"],
    ["PARAM-SECRET-001", "/parameters", "Secret parameter values are masked and reveal only after explicit action.", "parameters", "covered", "PARAM-SECRET-001; STRESS-PARAMETERS-001"],
    ["PARAM-SET-001", "/parameters", "Missing and existing parameter values can be set through resource commands.", "parameters", "covered", "PARAM-SET-001"],
    ["PARAM-NOTIFICATION-001", "/parameters", "The unresolved-parameters notification navigates to parameter entry.", "parameters", "covered", "PARAM-NOTIFICATION-001"],
    ["PARAM-SESSION-001", "/parameters", "Parameter filter, sort, and selected resource state are restorable.", "parameters", "covered", "PARAM-SESSION-001"],
  ],
  commands: [
    ["CMD-VISIBILITY-001", "/", "Enabled, disabled, hidden, UI-only, and API-only command visibility is honored.", "commands", "covered", "CMD-VISIBILITY-001; STRESS-COMMAND-VISIBILITY-001"],
    ["CMD-HIGHLIGHT-001", "/", "Highlighted commands remain directly available and other commands use overflow presentation.", "commands", "covered", "CMD-HIGHLIGHT-001"],
    ["CMD-ICON-001", "/", "Custom command icon names render in direct and overflow command surfaces.", null, "covered", "RES-ICON-001; STRESS-COMMAND-ICON-001"],
    ["CMD-ICON-VARIANT-001", "/", "Regular and filled command icon variants are preserved.", null, "covered", "TK-ICON-001; RES-ICON-001; STRESS-COMMAND-ICON-001"],
    ["CMD-DESCRIPTION-001", "/", "Command display names and descriptions remain visible and accessible.", "commands", "covered", "CMD-DESCRIPTION-001"],
    ["CMD-CONFIRM-001", "/", "Commands with confirmation messages require explicit confirmation.", "commands", "covered", "CMD-CONFIRM-001"],
    ["CMD-EXECUTE-001", "/", "Commands execute against the selected live resource and report success, cancellation, or failure.", null, "covered", "RES-COMMANDS-001; HTTP-COMMAND-001; HTTP-COMMAND-OUTCOMES-001; STRESS-COMMAND-EXECUTE-001"],
    ["CMD-TEXT-001", "/", "Text arguments support label, description, placeholder, required, and maximum length.", "commands", "covered", "RES-INTERACTION-001; HTTP-INTERACTION-001; STRESS-COMMAND-ARGUMENTS-001"],
    ["CMD-NUMBER-001", "/", "Number arguments preserve numeric values and validation.", "commands", "covered", "HTTP-INTERACTION-001; CMD-VALIDATION-001; STRESS-COMMAND-ARGUMENTS-001"],
    ["CMD-BOOLEAN-001", "/", "Boolean arguments preserve checked state and disabled state.", "commands", "covered", "RES-INTERACTION-001; HTTP-INTERACTION-001; STRESS-COMMAND-ARGUMENTS-001"],
    ["CMD-CHOICE-001", "/", "Choice arguments preserve options, display names, placeholders, and defaults.", "commands", "covered", "RES-INTERACTION-001; HTTP-INTERACTION-001; STRESS-COMMAND-ARGUMENTS-001"],
    ["CMD-CUSTOM-CHOICE-001", "/", "Choice arguments can allow a searchable custom value.", "commands", "covered", "TK-COMBOBOX-001; CMD-CUSTOM-CHOICE-001"],
    ["CMD-SECRET-001", "/", "Secret text arguments mask values, disable password saving, and support explicit reveal.", "commands", "covered", "HTTP-INTERACTION-001; TK-SECRET-INPUT-001; STRESS-COMMAND-ARGUMENTS-001"],
    ["CMD-DYNAMIC-001", "/", "Dependent argument choices load asynchronously when prerequisite values change.", "commands", "covered", "CMD-DYNAMIC-001"],
    ["CMD-LIVE-VALIDATION-001", "/", "Inputs can request server validation while values change.", "commands", "covered", "CMD-LIVE-VALIDATION-001"],
    ["CMD-VALIDATION-001", "/", "Field-level and form-level validation errors are announced and rendered.", "commands", "covered", "CMD-VALIDATION-001"],
    ["CMD-MANY-INPUTS-001", "/", "Large command forms remain scrollable and submit every input value.", "commands", "covered", "CMD-MANY-INPUTS-001"],
    ["CMD-MESSAGEBOX-001", "/", "Confirmation and message-box interactions support primary, secondary, dismiss, and intent.", "commands", "covered", "CMD-MESSAGEBOX-001"],
    ["CMD-NOTIFICATION-001", "/", "Interaction notifications support semantic intent, links, actions, and non-dismissible state.", "commands", "covered", "CMD-NOTIFICATION-001"],
    ["CMD-MARKDOWN-001", "/", "Interaction messages and field descriptions opt into sanitized Markdown rendering.", "commands", "covered", "TK-MARKDOWN-001; CMD-MARKDOWN-001"],
    ["CMD-RESULT-TEXT-001", "/", "Plain-text command results open in a readable visualizer and can be downloaded.", "commands", "covered", "CMD-RESULT-TEXT-001"],
    ["CMD-RESULT-JSON-001", "/", "JSON command results preserve formatting, masking, copy, and download behavior.", "commands", "covered", "CMD-RESULT-JSON-001"],
    ["CMD-RESULT-MARKDOWN-001", "/", "Markdown command results render tables and rich content safely.", "commands", "covered", "CMD-RESULT-MARKDOWN-001"],
    ["CMD-RESULT-IMMEDIATE-001", "/", "DisplayImmediately command results open without requiring a second action.", "commands", "covered", "CMD-RESULT-IMMEDIATE-001"],
    ["CMD-PROCESS-001", "/", "Process command stdout, stderr, exit status, line limits, stdin, environment, and working directory are represented.", "commands", "covered", "STRESS-PROCESS-COMMAND-001; CMD-RESULT-TEXT-001"],
  ],
  console: [
    ["CONSOLE-RESOURCE-001", "/consolelogs", "A grouped resource picker selects one resource or all resources.", "console", "covered", "CONSOLE-RESOURCE-001; CONSOLE-ALL-001; TK-SELECT-001"],
    ["CONSOLE-BACKLOG-001", "/consolelogs", "Selecting a resource loads the existing console backlog.", "console", "covered", "CONSOLE-STREAM-001; HTTP-CONSOLE-001; STRESS-CONSOLE-001"],
    ["CONSOLE-LIVE-001", "/consolelogs", "New stdout and stderr lines stream without reloading the page.", "console", "covered", "CONSOLE-STREAM-001; STRESS-CONSOLE-001"],
    ["CONSOLE-SWITCH-001", "/consolelogs", "Switching resources replaces the visible stream and subscription.", "console", "covered", "CONSOLE-SWITCH-001"],
    ["CONSOLE-FOLLOW-001", "/consolelogs", "Manual scrolling pauses tail-follow and the user can return to the live tail.", null, "covered", "CONSOLE-FOLLOW-001"],
    ["CONSOLE-PAUSE-001", "/consolelogs", "Incoming console data can be paused and resumed without losing context.", "console", "covered", "CONSOLE-PAUSE-001; CONSOLE-ROUTE-001"],
    ["CONSOLE-CLEAR-001", "/consolelogs", "Console data can be cleared for the selected resource or all resources.", "console", "covered", "CONSOLE-CLEAR-001"],
    ["CONSOLE-DOWNLOAD-001", "/consolelogs", "The current console log can be downloaded.", "console", "covered", "HTTP-CONSOLE-CONTROLS-001"],
    ["CONSOLE-TIMESTAMP-001", "/consolelogs", "Timestamp visibility and UTC/local formatting can be toggled.", "console", "covered", "HTTP-CONSOLE-CONTROLS-001; CONSOLE-ROUTE-001"],
    ["CONSOLE-WRAP-001", "/consolelogs", "Long console lines can wrap or scroll horizontally.", "console", "covered", "HTTP-CONSOLE-CONTROLS-001; CONSOLE-ROUTE-001"],
    ["CONSOLE-COMMANDS-001", "/consolelogs", "Commands for the selected resource are available from the console toolbar.", "console", "covered", "CONSOLE-COMMANDS-001"],
    ["CONSOLE-TERMINAL-001", "/consolelogs", "Interactive resources render a terminal and can take or release control.", null, "covered", "CONSOLE-TERMINAL-001; TERMINAL-LIVE-001; DeckApiTests.GetResources_ReturnsTerminalMetadata"],
    ["CONSOLE-TERMINAL-FONT-001", "/consolelogs", "Interactive terminal font size can be increased, decreased, and reset.", null, "covered", "CONSOLE-TERMINAL-FONT-001"],
    ["CONSOLE-TERMINAL-SIZE-001", "/consolelogs", "Interactive terminal column and row presets update the remote terminal size.", null, "covered", "CONSOLE-TERMINAL-SIZE-001"],
    ["CONSOLE-VIRTUALIZATION-001", "/consolelogs", "Large console streams remain responsive and preserve stable line numbers.", null, "covered", "HTTP-CONSOLE-VIRTUALIZATION-001"],
    ["CONSOLE-ROUTE-001", "/consolelogs/resource/{name}", "Selected resource and console options are deep-linkable and restorable.", "console", "covered", "CONSOLE-ROUTE-001"],
  ],
  "structured-logs": [
    ["LOG-LIST-001", "/structuredlogs", "Structured logs render resource, level, timestamp, message, trace, and actions columns.", "structured-logs", "covered", "LOG-LIST-001; HTTP-STRUCTURED-LOGS-001; STRESS-STRUCTURED-LOGS-001"],
    ["LOG-LIVE-001", "/structuredlogs", "New structured logs stream into the list and update totals.", "structured-logs", "covered", "LOG-LIVE-001; HTTP-STRUCTURED-LOGS-001; STRESS-STRUCTURED-LOGS-001"],
    ["LOG-RESOURCE-001", "/structuredlogs", "Logs filter through a grouped resource selector.", "structured-logs", "covered", "LOG-RESOURCE-001; STRESS-STRUCTURED-LOG-RESOURCE-001"],
    ["LOG-LEVEL-001", "/structuredlogs", "All supported severity levels can be selected.", "structured-logs", "covered", "LOG-SEVERITY-001"],
    ["LOG-TEXT-FILTER-001", "/structuredlogs", "Logs filter across resource and message content.", "structured-logs", "covered", "LOG-FILTER-001"],
    ["LOG-STRUCTURED-FILTER-001", "/structuredlogs", "Structured attribute filters can be added, edited, enabled, disabled, and removed.", "structured-logs", "covered", "LOG-STRUCTURED-FILTER-001; TK-STRUCTURED-FILTER-001"],
    ["LOG-FILTER-COUNT-001", "/structuredlogs", "Enabled structured filters expose a count and management menu.", null, "covered", "LOG-FILTER-COUNT-001"],
    ["LOG-PAUSE-001", "/structuredlogs", "Incoming structured logs can be paused and resumed.", "structured-logs", "covered", "LOG-PAUSE-001; STRESS-STRUCTURED-LOG-PAUSE-001"],
    ["LOG-CLEAR-001", "/structuredlogs", "Structured logs can be cleared for the selected resource or all resources.", "structured-logs", "covered", "HTTP-STRUCTURED-LOG-CLEAR-001; STRESS-STRUCTURED-LOG-CLEAR-001"],
    ["LOG-VIRTUALIZATION-001", "/structuredlogs", "Large log volumes remain responsive through row virtualization.", null, "covered", "HTTP-STRUCTURED-LOG-VIRTUALIZATION-001"],
    ["LOG-DETAILS-001", "/structuredlogs", "Selecting a log opens complete event, scope, resource, and attribute details.", "structured-logs", "covered", "LOG-DETAILS-001; HTTP-STRUCTURED-LOG-DETAILS-001; STRESS-STRUCTURED-LOG-DETAILS-001"],
    ["LOG-ACTIONS-001", "/structuredlogs", "Per-log actions expose details, text/JSON visualizers, copy, and related navigation.", null, "covered", "LOG-ACTIONS-001; LOG-TRACE-LINK-001; HTTP-STRUCTURED-LOG-DETAILS-001; STRESS-STRUCTURED-LOG-DETAILS-001"],
    ["LOG-TRACE-LINK-001", "/structuredlogs", "Trace IDs deep-link to the matching trace and span.", null, "covered", "LOG-TRACE-LINK-001; TRACE-DETAIL-ROUTE-001"],
    ["LOG-GENAI-001", "/structuredlogs", "GenAI log records open the dedicated GenAI visualizer.", null, "covered", "LOG-GENAI-001"],
    ["LOG-EXPLAIN-001", "/structuredlogs", "Explain errors summarizes current error logs through the assistant.", null, "covered", "LOG-EXPLAIN-001"],
    ["LOG-ROUTE-001", "/structuredlogs/resource/{name}", "Resource selection, filters, and selected log are deep-linkable and restorable.", null, "covered", "LOG-ROUTE-001"],
  ],
  traces: [
    ["TRACE-LIST-001", "/traces", "Traces render timestamp, name, span count, duration, error status, and actions.", "traces", "covered", "TRACE-LIST-001; TRACE-ACTIONS-001; HTTP-TRACES-001; STRESS-TRACES-001"],
    ["TRACE-LIVE-001", "/traces", "Incoming spans update trace groups and totals without reloading the page.", "traces", "covered", "TRACE-LIVE-001; HTTP-TRACES-001; STRESS-TRACES-001"],
    ["TRACE-RESOURCE-001", "/traces", "Traces filter through a grouped resource selector.", "traces", "covered", "TRACE-RESOURCE-001"],
    ["TRACE-TYPE-001", "/traces", "HTTP, database, messaging, RPC, GenAI, cloud, and other span types can be selected.", "traces", "covered", "TRACE-TYPE-001"],
    ["TRACE-TEXT-FILTER-001", "/traces", "Traces filter by operation, resource, and trace identifiers.", "traces", "covered", "TRACE-FILTER-001"],
    ["TRACE-STRUCTURED-FILTER-001", "/traces", "Structured trace and span filters can be composed and managed.", "traces", "covered", "TRACE-STRUCTURED-FILTER-001; TK-STRUCTURED-FILTER-001"],
    ["TRACE-DURATION-001", "/traces", "Trace and span duration is represented consistently at different scales.", "traces", "covered", "TRACE-DURATION-001; HTTP-TRACES-001"],
    ["TRACE-ERROR-001", "/traces", "Failed traces and spans expose status, tags, and error styling.", null, "covered", "TRACE-ERROR-001; TRACE-EVENTS-001; HTTP-TRACES-001"],
    ["TRACE-PAUSE-001", "/traces", "Incoming traces can be paused and resumed.", "traces", "covered", "TRACE-PAUSE-001"],
    ["TRACE-CLEAR-001", "/traces", "Trace data can be cleared for the selected resource or all resources.", "traces", "covered", "TRACE-CLEAR-001; HTTP-TRACE-CLEAR-001"],
    ["TRACE-VIRTUALIZATION-001", "/traces", "Large trace inventories remain responsive through virtualization.", null, "covered", "HTTP-TRACE-VIRTUALIZATION-001"],
    ["TRACE-ACTIONS-001", "/traces", "Per-trace actions expose detail, copy, and related telemetry navigation.", "traces", "covered", "TRACE-ACTIONS-001"],
    ["TRACE-DETAIL-ROUTE-001", "/traces/detail/{traceId}", "A trace opens on a stable deep-linked detail route.", "traces", "covered", "TRACE-DETAIL-ROUTE-001"],
    ["TRACE-TREE-001", "/traces/detail/{traceId}", "The trace detail preserves parent/child span hierarchy and chronological placement.", "traces", "covered", "TRACE-TREE-001; HTTP-TRACES-001"],
    ["TRACE-EXPAND-001", "/traces/detail/{traceId}", "Trace detail supports individual and expand-all/collapse-all span control.", "traces", "covered", "TRACE-EXPAND-001"],
    ["TRACE-SPAN-DETAILS-001", "/traces/detail/{traceId}", "Span details include identifiers, timing, status, attributes, resource, and instrumentation scope.", "traces", "covered", "TRACE-DETAILS-001; HTTP-TRACES-001; STRESS-TRACES-001"],
    ["TRACE-EVENTS-001", "/traces/detail/{traceId}", "Span events and exception details preserve timestamps and attributes.", null, "covered", "TRACE-EVENTS-001; HTTP-TRACES-001"],
    ["TRACE-LINKS-001", "/traces/detail/{traceId}", "Span links navigate to related traces and preserve link attributes.", null, "covered", "TRACE-LINKS-001; HTTP-TRACES-001"],
    ["TRACE-GENAI-001", "/traces/detail/{traceId}", "GenAI spans and traces open the dedicated GenAI visualizer.", null, "covered", "TRACE-GENAI-001"],
    ["TRACE-EXPLAIN-001", "/traces", "Explain errors summarizes current failed traces through the assistant.", null, "covered", "TRACE-EXPLAIN-001"],
    ["TRACE-SESSION-001", "/traces", "Resource, type, filters, and selection are deep-linkable and restorable.", null, "covered", "TRACE-SESSION-001; TRACE-DETAIL-ROUTE-001"],
  ],
  metrics: [
    ["METRIC-RESOURCE-001", "/metrics", "Metrics require and preserve a selected telemetry resource.", "metrics", "covered", "METRIC-RESOURCE-001; METRIC-SESSION-001"],
    ["METRIC-TREE-001", "/metrics", "Meters and instruments render in a searchable hierarchical selector.", null, "covered", "METRIC-TREE-001"],
    ["METRIC-METADATA-001", "/metrics", "Instrument name, description, unit, type, and meter metadata are visible.", null, "covered", "METRIC-METADATA-001"],
    ["METRIC-DURATION-001", "/metrics", "One minute through twelve hour duration presets update the query window.", "metrics", "covered", "METRIC-RANGE-001; METRIC-SESSION-001"],
    ["METRIC-PAUSE-001", "/metrics", "Incoming metrics can be paused and resumed.", "metrics", "covered", "METRIC-PAUSE-001; METRIC-SESSION-001"],
    ["METRIC-CLEAR-001", "/metrics", "Metric data can be cleared for the selected resource or all resources.", "metrics", "covered", "METRIC-CLEAR-001; DeckApiTests.DeleteMetrics_ClearsSelectedOrAllResources"],
    ["METRIC-CHART-001", "/metrics", "Instrument samples render as an interactive time-series chart.", "metrics", "covered", "METRIC-CHART-001; METRIC-CURSOR-001; METRIC-ZOOM-001"],
    ["METRIC-TABLE-001", "/metrics", "Metric data can switch between chart and table representations.", "metrics", "covered", "METRIC-TABLE-001; METRIC-SESSION-001"],
    ["METRIC-DIMENSIONS-001", "/metrics", "Dimension filters and multiple attribute series are discoverable and selectable.", null, "covered", "METRIC-DIMENSIONS-001; DeckApiTests.GetMetrics_ReturnsSummariesAndSeries"],
    ["METRIC-HISTOGRAM-001", "/metrics", "Histogram count, sum, buckets, and percentile views preserve aggregation semantics.", null, "covered", "METRIC-HISTOGRAM-001; HTTP-METRICS-001; DeckApiTests.GetMetrics_ReturnsSummariesAndSeries; ChartDataCalculatorTests.TryCalculateHistogramAggregatePoint_ReturnsCumulativeDeltas"],
    ["METRIC-EXEMPLARS-001", "/metrics", "Metric exemplars expose values, timestamps, attributes, and related trace navigation.", null, "covered", "METRIC-EXEMPLARS-001; DeckApiTests.GetMetrics_ReturnsSummariesAndSeries"],
    ["METRIC-CURSOR-001", "/metrics", "Hovering charts exposes aligned timestamp and series values.", null, "covered", "METRIC-CURSOR-001"],
    ["METRIC-ZOOM-001", "/metrics", "Dragging to zoom preserves inspection state and pauses live updates.", null, "covered", "METRIC-ZOOM-001"],
    ["METRIC-ROUTES-001", "/metrics/resource/{resource}?meter={meter}&instrument={instrument}", "Resource, meter, instrument, duration, and view are deep-linkable.", "metrics", "covered", "METRIC-ROUTES-001; METRIC-SESSION-001"],
    ["METRIC-EMPTY-001", "/metrics", "Loading, no-resource, no-meter, no-instrument, and no-data states are distinct.", "metrics", "covered", "HTTP-EMPTY-TELEMETRY-001; STRESS-EMPTY-TELEMETRY-001"],
    ["METRIC-SESSION-001", "/metrics", "Metric selection, duration, view, dimensions, and zoom are restorable.", null, "covered", "METRIC-SESSION-001"],
  ],
} as const satisfies Record<DashboardArea, readonly FeatureDefinition[]>;

export const dashboardParityFeatures: readonly DashboardParityFeature[] = (
  Object.entries(featureDefinitions) as [DashboardArea, readonly FeatureDefinition[]][]
).flatMap(([area, definitions]) => definitions.map(([
  id,
  legacyRoute,
  description,
  legacyScenario,
  reactStatus,
  currentCoverage,
]) => ({
  id,
  area,
  legacyRoute,
  description,
  legacyScenario,
  reactStatus,
  currentCoverage: currentCoverage ?? null,
})));

export function getLegacyScenarioFeatures(scenario: LegacyScenario): DashboardParityFeature[] {
  return dashboardParityFeatures.filter((feature) => feature.legacyScenario === scenario);
}

export function getUncoveredLegacyFeatures(): DashboardParityFeature[] {
  return dashboardParityFeatures.filter((feature) => feature.legacyScenario === null);
}

export function getReactParityGaps(): DashboardParityFeature[] {
  return dashboardParityFeatures.filter((feature) => feature.reactStatus !== "covered");
}
