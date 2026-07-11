# Dashboard migration parity ledger

- Total legacy features: 157
- React covered: 31
- React partial: 43
- React missing: 83
- Legacy black-box scenarios pending: 80
- React parity gaps: 126

| ID | Area | Legacy route | Legacy test | React | Current coverage | Behavior |
| --- | --- | --- | --- | --- | --- | --- |
| SHELL-IDENTITY-001 | shell | `/` | shell | covered | APP-SHELL-001; STRESS-CONFIG-001 | Application identity and dashboard version are visible. |
| SHELL-NAV-001 | shell | `/` | shell | partial | APP-NAV-001 | Resources, Parameters, Graph, Console, Structured Logs, Traces, and Metrics are reachable from navigation. |
| SHELL-ROUTES-001 | shell | `/` | PENDING | missing | - | Pages have stable URLs and browser history/deep links restore the selected page. |
| SHELL-REPO-001 | shell | `/` | shell | missing | - | The Aspire repository link is available from the top bar. |
| SHELL-HELP-001 | shell | `/` | shell | missing | - | Help opens with documentation and keyboard shortcut reference content. |
| SHELL-KEYBOARD-001 | shell | `/` | PENDING | missing | - | Page navigation, panel, help, and settings keyboard shortcuts work. |
| SHELL-AGENTS-001 | shell | `/` | shell | missing | - | The AI agents entry point appears when enabled. |
| SHELL-ASSISTANT-001 | shell | `/` | PENDING | missing | - | The AI assistant opens, closes, expands, starts a new chat, and sends or stops responses. |
| SHELL-NOTIFICATIONS-001 | shell | `/` | shell | partial | APP-NOTIFICATION-001 | Active notifications render intent, actions, links, and dismiss behavior. |
| SHELL-NOTIFICATION-CENTER-001 | shell | `/` | PENDING | missing | - | The notification center opens and preserves notification history. |
| SHELL-SETTINGS-001 | shell | `/` | shell | missing | - | Settings opens from the top bar and reports dashboard/runtime versions. |
| SHELL-THEME-001 | shell | `/` | shell | partial | APP-THEME-001 | System, light, and dark theme selection is persisted. |
| SHELL-LANGUAGE-001 | shell | `/` | shell | missing | - | The dashboard language can be selected and applied. |
| SHELL-TIME-FORMAT-001 | shell | `/` | shell | missing | - | System, 12-hour, and 24-hour time formatting can be selected. |
| SHELL-MANAGE-DATA-001 | shell | `/` | PENDING | missing | - | Resource logs and telemetry can be inspected, exported, imported, and cleared. |
| SHELL-USER-001 | shell | `/` | PENDING | missing | - | Authenticated user profile and sign-out behavior are available when configured. |
| SHELL-AUTH-001 | shell | `/login` | PENDING | missing | - | Browser-token and OpenID Connect login flows protect the frontend. |
| SHELL-RECONNECT-001 | shell | `/` | PENDING | missing | - | A lost dashboard circuit or backend connection exposes reconnect and recovery UI. |
| SHELL-UNSECURED-001 | shell | `/` | shell | missing | - | An unsecured telemetry/API endpoint warning is visible with supporting guidance. |
| SHELL-UPDATE-001 | shell | `/` | shell | missing | - | Available dashboard updates show instructions, ignore, and dismiss actions. |
| SHELL-NOTFOUND-001 | shell | `/error/404` | PENDING | missing | - | Unknown routes render a dedicated not-found experience. |
| SHELL-ERROR-001 | shell | `/error` | PENDING | missing | - | Unhandled errors render a recoverable error experience. |
| SHELL-RESPONSIVE-001 | shell | `/` | shell | partial | APP-RESPONSIVE-001; STRESS-RESPONSIVE-001 | Navigation, header actions, pages, and overlays remain usable on mobile. |
| SHELL-ACCESSIBILITY-001 | shell | `/` | shell | partial | APP-PAGE-001; toolkit.aria.yml | Landmarks, names, focus order, dialogs, and keyboard interaction remain accessible. |
| SHELL-BROWSER-ERRORS-001 | shell | `/` | shell | covered | APP-BROWSER-001 | Normal navigation and interaction produce no page or console errors. |
| RES-LIST-001 | resources | `/` | resources | covered | RES-LIST-001; STRESS-RESOURCES-001 | The live resource inventory renders and excludes parameter resources. |
| RES-HIDDEN-001 | resources | `/` | resources | partial | STRESS-VISIBILITY-001 | Hidden resources are excluded by default and can be shown through view options. |
| RES-COUNT-001 | resources | `/` | resources | covered | STRESS-RESOURCES-001 | The visible resource count tracks filtering and view options. |
| RES-TEXT-FILTER-001 | resources | `/` | resources | covered | RES-FILTER-001 | Resources filter by text and expose an empty result state. |
| RES-STRUCTURED-FILTER-001 | resources | `/` | resources | missing | - | Resource state, type, and health filters can be composed and cleared. |
| RES-VIEW-OPTIONS-001 | resources | `/` | resources | missing | - | View options control hidden resources, resource type, and hierarchy expansion. |
| RES-SORT-001 | resources | `/` | resources | covered | RES-SORT-001 | Supported resource columns sort ascending and descending. |
| RES-COLUMNS-001 | resources | `/` | resources | partial | RES-LIST-001; RES-ENDPOINT-001 | Name, state, source, URLs, and start time columns render the legacy data model. |
| RES-SOURCE-001 | resources | `/` | resources | missing | - | Project, executable, container, and custom resource sources are displayed. |
| RES-URLS-001 | resources | `/` | resources | partial | RES-ENDPOINT-001 | External endpoints use display names and preserve internal/inactive endpoint rules. |
| RES-LONG-URLS-001 | resources | `/` | PENDING | partial | APP-RESPONSIVE-001 | Large endpoint sets and very long URLs remain usable without breaking layout. |
| RES-NESTING-001 | resources | `/` | resources | missing | - | Parent/child resources render as a hierarchical tree. |
| RES-EXPAND-001 | resources | `/` | resources | missing | - | Individual branches and all branches can be expanded and collapsed. |
| RES-GRAPH-001 | resources | `/?view=Graph` | resources | missing | - | Resources and relationships render in the graph view. |
| RES-GRAPH-ZOOM-001 | resources | `/?view=Graph` | resources | missing | - | Graph zoom in, zoom out, and reset controls work. |
| RES-GRAPH-CONTEXT-001 | resources | `/?view=Graph` | PENDING | missing | - | Graph nodes expose resource actions and details context menus. |
| RES-VIRTUALIZATION-001 | resources | `/` | PENDING | missing | - | Large resource inventories remain responsive through row virtualization. |
| RES-DETAILS-001 | resources | `/` | resources | covered | RES-DETAILS-001; STRESS-DETAILS-001 | Selecting a resource opens overview, endpoints, properties, environment, health, and relationships. |
| RES-DETAILS-LINK-001 | resources | `/?resource={name}` | PENDING | missing | - | A resource details selection is deep-linkable and restorable. |
| RES-PROPERTIES-001 | resources | `/` | resources | partial | RES-DETAILS-001 | Known, custom, highlighted, null, array, and object properties render correctly. |
| RES-SECRETS-001 | resources | `/` | resources | covered | RES-SECRETS-001; STRESS-SECRETS-001 | Sensitive properties and environment values remain masked until explicitly revealed. |
| RES-COPY-001 | resources | `/` | PENDING | missing | - | Resource property and environment values can be copied without accidental disclosure. |
| RES-HEALTH-001 | resources | `/` | resources | covered | RES-DETAILS-001 | Health summaries and individual health reports preserve status and descriptions. |
| RES-RELATIONSHIPS-001 | resources | `/` | resources | covered | RES-DETAILS-001 | Parent, child, wait, reference, and other relationships are visible. |
| RES-STATE-001 | resources | `/` | resources | partial | STRESS-RESOURCES-001 | Running, starting, finished, exited, not-started, and unknown states remain distinguishable. |
| RES-NO-STATUS-001 | resources | `/` | resources | partial | STRESS-RESOURCES-001 | Resources without status data render a stable unknown state. |
| RES-RESOURCE-ICON-001 | resources | `/` | PENDING | covered | RES-ICON-001; HTTP-RESOURCES-001; STRESS-RESOURCE-ICON-001 | Custom Fluent resource icon names override resource-type fallbacks. |
| RES-RESOURCE-ICON-VARIANT-001 | resources | `/` | PENDING | covered | RES-ICON-001; DeckApiTests.GetResources_ReturnsDeckResourceContract | Regular and filled resource icon variants are preserved. |
| RES-CONTEXT-MENU-001 | resources | `/` | PENDING | missing | - | Resource rows expose details, navigation, and commands through a context menu. |
| RES-SESSION-001 | resources | `/` | PENDING | missing | - | Search, filters, sort, view, expansion, and selection survive navigation and reload. |
| PARAM-LIST-001 | parameters | `/parameters` | parameters | covered | PARAM-LIST-001; STRESS-PARAMETERS-001 | Plain, secret, and unresolved parameters render on a dedicated page. |
| PARAM-COUNT-001 | parameters | `/parameters` | parameters | covered | STRESS-PARAMETERS-001 | The parameter count tracks the current filter. |
| PARAM-FILTER-001 | parameters | `/parameters` | parameters | covered | PARAM-FILTER-001 | Parameters filter by name and state. |
| PARAM-SORT-001 | parameters | `/parameters` | parameters | covered | PARAM-SORT-001 | Supported parameter columns sort ascending and descending. |
| PARAM-MISSING-001 | parameters | `/parameters` | parameters | covered | PARAM-LIST-001; STRESS-PARAMETERS-001 | Unresolved parameters expose the value-missing state and placeholder. |
| PARAM-SECRET-001 | parameters | `/parameters` | parameters | covered | PARAM-SECRET-001; STRESS-PARAMETERS-001 | Secret parameter values are masked and reveal only after explicit action. |
| PARAM-SET-001 | parameters | `/parameters` | PENDING | partial | RES-COMMANDS-001 | Missing and existing parameter values can be set through resource commands. |
| PARAM-NOTIFICATION-001 | parameters | `/parameters` | PENDING | missing | - | The unresolved-parameters notification navigates to parameter entry. |
| PARAM-SESSION-001 | parameters | `/parameters` | PENDING | missing | - | Parameter filter, sort, and selected resource state are restorable. |
| CMD-VISIBILITY-001 | commands | `/` | PENDING | partial | RES-COMMANDS-001 | Enabled, disabled, hidden, UI-only, and API-only command visibility is honored. |
| CMD-HIGHLIGHT-001 | commands | `/` | commands | partial | RES-ACTION-MENU-001 | Highlighted commands remain directly available and other commands use overflow presentation. |
| CMD-ICON-001 | commands | `/` | PENDING | covered | RES-ICON-001; STRESS-COMMAND-ICON-001 | Custom command icon names render in direct and overflow command surfaces. |
| CMD-ICON-VARIANT-001 | commands | `/` | PENDING | covered | TK-ICON-001; RES-ICON-001; STRESS-COMMAND-ICON-001 | Regular and filled command icon variants are preserved. |
| CMD-DESCRIPTION-001 | commands | `/` | commands | partial | RES-ACTION-MENU-001 | Command display names and descriptions remain visible and accessible. |
| CMD-CONFIRM-001 | commands | `/` | PENDING | partial | RES-CONFIRM-001 | Commands with confirmation messages require explicit confirmation. |
| CMD-EXECUTE-001 | commands | `/` | PENDING | partial | RES-COMMANDS-001; HTTP-COMMAND-001; STRESS-COMMAND-EXECUTE-001 | Commands execute against the selected live resource and report success, cancellation, or failure. |
| CMD-TEXT-001 | commands | `/` | commands | covered | RES-INTERACTION-001; HTTP-INTERACTION-001; STRESS-COMMAND-ARGUMENTS-001 | Text arguments support label, description, placeholder, required, and maximum length. |
| CMD-NUMBER-001 | commands | `/` | commands | partial | HTTP-INTERACTION-001; STRESS-COMMAND-ARGUMENTS-001 | Number arguments preserve numeric values and validation. |
| CMD-BOOLEAN-001 | commands | `/` | commands | partial | RES-INTERACTION-001; HTTP-INTERACTION-001; STRESS-COMMAND-ARGUMENTS-001 | Boolean arguments preserve checked state and disabled state. |
| CMD-CHOICE-001 | commands | `/` | commands | partial | RES-INTERACTION-001; HTTP-INTERACTION-001; STRESS-COMMAND-ARGUMENTS-001 | Choice arguments preserve options, display names, placeholders, and defaults. |
| CMD-CUSTOM-CHOICE-001 | commands | `/` | PENDING | missing | - | Choice arguments can allow a searchable custom value. |
| CMD-SECRET-001 | commands | `/` | commands | partial | HTTP-INTERACTION-001; STRESS-COMMAND-ARGUMENTS-001 | Secret text arguments mask values, disable password saving, and support explicit reveal. |
| CMD-DYNAMIC-001 | commands | `/` | PENDING | missing | - | Dependent argument choices load asynchronously when prerequisite values change. |
| CMD-LIVE-VALIDATION-001 | commands | `/` | PENDING | partial | RES-INTERACTION-001 | Inputs can request server validation while values change. |
| CMD-VALIDATION-001 | commands | `/` | PENDING | partial | RES-INTERACTION-001 | Field-level and form-level validation errors are announced and rendered. |
| CMD-MANY-INPUTS-001 | commands | `/` | PENDING | missing | - | Large command forms remain scrollable and submit every input value. |
| CMD-MESSAGEBOX-001 | commands | `/` | PENDING | partial | APP-NOTIFICATION-001 | Confirmation and message-box interactions support primary, secondary, dismiss, and intent. |
| CMD-NOTIFICATION-001 | commands | `/` | PENDING | partial | APP-NOTIFICATION-001 | Interaction notifications support semantic intent, links, actions, and non-dismissible state. |
| CMD-MARKDOWN-001 | commands | `/` | PENDING | missing | - | Interaction messages and field descriptions opt into sanitized Markdown rendering. |
| CMD-RESULT-TEXT-001 | commands | `/` | PENDING | missing | - | Plain-text command results open in a readable visualizer and can be downloaded. |
| CMD-RESULT-JSON-001 | commands | `/` | PENDING | missing | - | JSON command results preserve formatting, masking, copy, and download behavior. |
| CMD-RESULT-MARKDOWN-001 | commands | `/` | PENDING | missing | - | Markdown command results render tables and rich content safely. |
| CMD-RESULT-IMMEDIATE-001 | commands | `/` | PENDING | missing | - | DisplayImmediately command results open without requiring a second action. |
| CMD-PROCESS-001 | commands | `/` | PENDING | missing | - | Process command stdout, stderr, exit status, line limits, stdin, environment, and working directory are represented. |
| CONSOLE-RESOURCE-001 | console | `/consolelogs` | console | partial | CONSOLE-RESOURCE-001 | A grouped resource picker selects one resource or all resources. |
| CONSOLE-BACKLOG-001 | console | `/consolelogs` | console | covered | CONSOLE-STREAM-001; HTTP-CONSOLE-001; STRESS-CONSOLE-001 | Selecting a resource loads the existing console backlog. |
| CONSOLE-LIVE-001 | console | `/consolelogs` | console | partial | CONSOLE-STREAM-001 | New stdout and stderr lines stream without reloading the page. |
| CONSOLE-SWITCH-001 | console | `/consolelogs` | PENDING | partial | CONSOLE-SWITCH-001 | Switching resources replaces the visible stream and subscription. |
| CONSOLE-FOLLOW-001 | console | `/consolelogs` | PENDING | covered | CONSOLE-FOLLOW-001 | Manual scrolling pauses tail-follow and the user can return to the live tail. |
| CONSOLE-PAUSE-001 | console | `/consolelogs` | console | missing | - | Incoming console data can be paused and resumed without losing context. |
| CONSOLE-CLEAR-001 | console | `/consolelogs` | console | missing | - | Console data can be cleared for the selected resource or all resources. |
| CONSOLE-DOWNLOAD-001 | console | `/consolelogs` | PENDING | missing | - | The current console log can be downloaded. |
| CONSOLE-TIMESTAMP-001 | console | `/consolelogs` | PENDING | missing | - | Timestamp visibility and UTC/local formatting can be toggled. |
| CONSOLE-WRAP-001 | console | `/consolelogs` | PENDING | missing | - | Long console lines can wrap or scroll horizontally. |
| CONSOLE-COMMANDS-001 | console | `/consolelogs` | PENDING | missing | - | Commands for the selected resource are available from the console toolbar. |
| CONSOLE-TERMINAL-001 | console | `/consolelogs` | PENDING | missing | - | Interactive resources render a terminal and can take or release control. |
| CONSOLE-TERMINAL-FONT-001 | console | `/consolelogs` | PENDING | missing | - | Interactive terminal font size can be increased, decreased, and reset. |
| CONSOLE-TERMINAL-SIZE-001 | console | `/consolelogs` | PENDING | missing | - | Interactive terminal column and row presets update the remote terminal size. |
| CONSOLE-VIRTUALIZATION-001 | console | `/consolelogs` | PENDING | partial | CONSOLE-STREAM-001 | Large console streams remain responsive and preserve stable line numbers. |
| CONSOLE-ROUTE-001 | console | `/consolelogs/resource/{name}` | PENDING | missing | - | Selected resource and console options are deep-linkable and restorable. |
| LOG-LIST-001 | structured-logs | `/structuredlogs` | structured-logs | partial | LOG-LIST-001 | Structured logs render resource, level, timestamp, message, trace, and actions columns. |
| LOG-LIVE-001 | structured-logs | `/structuredlogs` | structured-logs | covered | LOG-LIVE-001; HTTP-STRUCTURED-LOGS-001; STRESS-STRUCTURED-LOGS-001 | New structured logs stream into the list and update totals. |
| LOG-RESOURCE-001 | structured-logs | `/structuredlogs` | structured-logs | covered | LOG-RESOURCE-001; STRESS-STRUCTURED-LOG-RESOURCE-001 | Logs filter through a grouped resource selector. |
| LOG-LEVEL-001 | structured-logs | `/structuredlogs` | structured-logs | covered | LOG-SEVERITY-001 | All supported severity levels can be selected. |
| LOG-TEXT-FILTER-001 | structured-logs | `/structuredlogs` | structured-logs | covered | LOG-FILTER-001 | Logs filter across resource and message content. |
| LOG-STRUCTURED-FILTER-001 | structured-logs | `/structuredlogs` | structured-logs | missing | - | Structured attribute filters can be added, edited, enabled, disabled, and removed. |
| LOG-FILTER-COUNT-001 | structured-logs | `/structuredlogs` | PENDING | missing | - | Enabled structured filters expose a count and management menu. |
| LOG-PAUSE-001 | structured-logs | `/structuredlogs` | structured-logs | covered | LOG-PAUSE-001; STRESS-STRUCTURED-LOG-PAUSE-001 | Incoming structured logs can be paused and resumed. |
| LOG-CLEAR-001 | structured-logs | `/structuredlogs` | structured-logs | missing | - | Structured logs can be cleared for the selected resource or all resources. |
| LOG-VIRTUALIZATION-001 | structured-logs | `/structuredlogs` | PENDING | missing | - | Large log volumes remain responsive through row virtualization. |
| LOG-DETAILS-001 | structured-logs | `/structuredlogs` | PENDING | missing | - | Selecting a log opens complete event, scope, resource, and attribute details. |
| LOG-ACTIONS-001 | structured-logs | `/structuredlogs` | structured-logs | missing | - | Per-log actions expose details, text/JSON visualizers, copy, and related navigation. |
| LOG-TRACE-LINK-001 | structured-logs | `/structuredlogs` | PENDING | missing | - | Trace IDs deep-link to the matching trace and span. |
| LOG-GENAI-001 | structured-logs | `/structuredlogs` | PENDING | missing | - | GenAI log records open the dedicated GenAI visualizer. |
| LOG-EXPLAIN-001 | structured-logs | `/structuredlogs` | PENDING | missing | - | Explain errors summarizes current error logs through the assistant. |
| LOG-ROUTE-001 | structured-logs | `/structuredlogs/resource/{name}` | PENDING | missing | - | Resource selection, filters, and selected log are deep-linkable and restorable. |
| TRACE-LIST-001 | traces | `/traces` | traces | partial | TRACE-LIST-001 | Traces render timestamp, name, span count, duration, error status, and actions. |
| TRACE-RESOURCE-001 | traces | `/traces` | traces | missing | - | Traces filter through a grouped resource selector. |
| TRACE-TYPE-001 | traces | `/traces` | traces | missing | - | HTTP, database, messaging, RPC, GenAI, cloud, and other span types can be selected. |
| TRACE-TEXT-FILTER-001 | traces | `/traces` | traces | covered | TRACE-FILTER-001 | Traces filter by operation, resource, and trace identifiers. |
| TRACE-STRUCTURED-FILTER-001 | traces | `/traces` | traces | missing | - | Structured trace and span filters can be composed and managed. |
| TRACE-DURATION-001 | traces | `/traces` | traces | partial | TRACE-DURATION-001 | Trace and span duration is represented consistently at different scales. |
| TRACE-ERROR-001 | traces | `/traces` | PENDING | partial | TRACE-ERROR-001 | Failed traces and spans expose status, tags, and error styling. |
| TRACE-PAUSE-001 | traces | `/traces` | traces | missing | - | Incoming traces can be paused and resumed. |
| TRACE-CLEAR-001 | traces | `/traces` | traces | missing | - | Trace data can be cleared for the selected resource or all resources. |
| TRACE-VIRTUALIZATION-001 | traces | `/traces` | PENDING | missing | - | Large trace inventories remain responsive through virtualization. |
| TRACE-ACTIONS-001 | traces | `/traces` | traces | missing | - | Per-trace actions expose detail, copy, and related telemetry navigation. |
| TRACE-DETAIL-ROUTE-001 | traces | `/traces/detail/{traceId}` | PENDING | missing | - | A trace opens on a stable deep-linked detail route. |
| TRACE-TREE-001 | traces | `/traces/detail/{traceId}` | PENDING | partial | TRACE-LIST-001; TRACE-COLLAPSE-001 | The trace detail preserves parent/child span hierarchy and chronological placement. |
| TRACE-EXPAND-001 | traces | `/traces/detail/{traceId}` | PENDING | partial | TRACE-COLLAPSE-001 | Trace detail supports individual and expand-all/collapse-all span control. |
| TRACE-SPAN-DETAILS-001 | traces | `/traces/detail/{traceId}` | PENDING | partial | TRACE-DETAILS-001 | Span details include identifiers, timing, status, attributes, resource, and instrumentation scope. |
| TRACE-EVENTS-001 | traces | `/traces/detail/{traceId}` | PENDING | missing | - | Span events and exception details preserve timestamps and attributes. |
| TRACE-LINKS-001 | traces | `/traces/detail/{traceId}` | PENDING | missing | - | Span links navigate to related traces and preserve link attributes. |
| TRACE-GENAI-001 | traces | `/traces/detail/{traceId}` | PENDING | missing | - | GenAI spans and traces open the dedicated GenAI visualizer. |
| TRACE-EXPLAIN-001 | traces | `/traces` | PENDING | missing | - | Explain errors summarizes current failed traces through the assistant. |
| TRACE-SESSION-001 | traces | `/traces` | PENDING | missing | - | Resource, type, filters, and selection are deep-linkable and restorable. |
| METRIC-RESOURCE-001 | metrics | `/metrics` | metrics | missing | - | Metrics require and preserve a selected telemetry resource. |
| METRIC-TREE-001 | metrics | `/metrics` | PENDING | missing | - | Meters and instruments render in a searchable hierarchical selector. |
| METRIC-METADATA-001 | metrics | `/metrics` | PENDING | partial | METRIC-SELECT-001 | Instrument name, description, unit, type, and meter metadata are visible. |
| METRIC-DURATION-001 | metrics | `/metrics` | metrics | partial | METRIC-RANGE-001 | One minute through twelve hour duration presets update the query window. |
| METRIC-PAUSE-001 | metrics | `/metrics` | metrics | partial | METRIC-PAUSE-001 | Incoming metrics can be paused and resumed. |
| METRIC-CLEAR-001 | metrics | `/metrics` | metrics | missing | - | Metric data can be cleared for the selected resource or all resources. |
| METRIC-CHART-001 | metrics | `/metrics` | PENDING | partial | METRIC-CHART-001 | Instrument samples render as an interactive time-series chart. |
| METRIC-TABLE-001 | metrics | `/metrics` | PENDING | missing | - | Metric data can switch between chart and table representations. |
| METRIC-DIMENSIONS-001 | metrics | `/metrics` | PENDING | missing | - | Dimension filters and multiple attribute series are discoverable and selectable. |
| METRIC-HISTOGRAM-001 | metrics | `/metrics` | PENDING | partial | METRIC-CHART-001 | Histogram count, sum, buckets, and percentile views preserve aggregation semantics. |
| METRIC-EXEMPLARS-001 | metrics | `/metrics` | PENDING | missing | - | Metric exemplars expose values, timestamps, attributes, and related trace navigation. |
| METRIC-CURSOR-001 | metrics | `/metrics` | PENDING | covered | METRIC-CURSOR-001 | Hovering charts exposes aligned timestamp and series values. |
| METRIC-ZOOM-001 | metrics | `/metrics` | PENDING | covered | METRIC-ZOOM-001 | Dragging to zoom preserves inspection state and pauses live updates. |
| METRIC-ROUTES-001 | metrics | `/metrics/resource/{resource}/meter/{meter}/instrument/{instrument}` | PENDING | missing | - | Resource, meter, instrument, duration, and view are deep-linkable. |
| METRIC-EMPTY-001 | metrics | `/metrics` | metrics | partial | HTTP-EMPTY-TELEMETRY-001; STRESS-EMPTY-TELEMETRY-001 | Loading, no-resource, no-meter, no-instrument, and no-data states are distinct. |
| METRIC-SESSION-001 | metrics | `/metrics` | PENDING | missing | - | Metric selection, duration, view, dimensions, and zoom are restorable. |
