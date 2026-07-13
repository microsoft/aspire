import { useMemo, useRef, useState } from "react";
import {
  Accordion,
  Badge,
  Button,
  Checkbox,
  ComboBox,
  CommandMenu,
  ConfirmDialog,
  DataTable,
  Divider,
  Dialog,
  Drawer,
  EmptyState,
  FilterIcon,
  FilterMenu,
  ForceGraph,
  IconButton,
  Highlighter,
  MoonIcon,
  MoreIcon,
  NamedIcon,
  namedIconMappings,
  MarkdownContent,
  NotificationStack,
  Page,
  PageActions,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
  PageToolbar,
  PlayIcon,
  PropertyExplorer,
  PropertyGrid,
  ResourcesIcon,
  ResetViewIcon,
  ResourceTypeIcon,
  RestartIcon,
  SearchBox,
  SecretInput,
  SecretValue,
  Select,
  StateDot,
  StopIcon,
  StructuredFilterControl,
  SunIcon,
  Switch,
  Tabs,
  TextViewerDialog,
  ZoomInIcon,
  ZoomOutIcon,
  type Column,
  type ConfirmRequest,
  type DeckTheme,
  type ForceGraphHandle,
  type TextViewerRequest,
} from "../toolkit";
import type { TelemetryFilter } from "../lib/telemetryFilters";

interface SampleResource {
  name: string;
  type: string;
  state: string;
  stateStyle: string | null;
  health: string | null;
}

const resources: SampleResource[] = [
  { name: "frontend", type: "Project", state: "Running", stateStyle: null, health: "Healthy" },
  { name: "catalog-db", type: "Container", state: "Running", stateStyle: null, health: "Degraded" },
  { name: "migration", type: "Executable", state: "Exited", stateStyle: null, health: null },
];

const defaultIconNames = namedIconMappings.map((mapping) => mapping.name);
const iconMappingsByName = new Map(
  namedIconMappings.map((mapping) => [mapping.name.toLowerCase(), mapping]),
);

function getIconNames(): string[] {
  const requestedNames = new URLSearchParams(window.location.search).get("icons");
  if (!requestedNames) {
    return defaultIconNames;
  }

  return [...new Set(requestedNames.split(",").map((name) => name.trim()).filter(Boolean))];
}

const columns: Column<SampleResource>[] = [
  {
    key: "state",
    header: "State",
    render: (resource) => <StateDot state={resource.state} stateStyle={resource.stateStyle} health={resource.health} />,
    width: "240px",
  },
  {
    key: "name",
    header: "Name",
    render: (resource) => <span className="cell-name">{resource.name}</span>,
    compare: (left, right) => left.name.localeCompare(right.name),
  },
  { key: "type", header: "Type", render: (resource) => <Badge>{resource.type}</Badge>, width: "180px" },
];

export function ToolkitPlayground({
  theme,
  onToggleTheme,
}: {
  theme: DeckTheme;
  onToggleTheme: () => void;
}) {
  const [query, setQuery] = useState("");
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [confirmation, setConfirmation] = useState<ConfirmRequest | null>(null);
  const [lastAction, setLastAction] = useState("No action selected");
  const [notificationVisible, setNotificationVisible] = useState(false);
  const [environment, setEnvironment] = useState("development");
  const [region, setRegion] = useState("central");
  const [includeHidden, setIncludeHidden] = useState(false);
  const [pauseIncoming, setPauseIncoming] = useState(false);
  const [secretInput, setSecretInput] = useState("");
  const [selectedTab, setSelectedTab] = useState("overview");
  const [openAccordionItems, setOpenAccordionItems] = useState(["environment"]);
  const [pageRefreshCount, setPageRefreshCount] = useState(0);
  const [selectedResource, setSelectedResource] = useState<string | null>(null);
  const [textViewer, setTextViewer] = useState<TextViewerRequest | null>(null);
  const [hiddenSampleTypes, setHiddenSampleTypes] = useState<string[]>([]);
  const [structuredFilters, setStructuredFilters] = useState<TelemetryFilter[]>([]);
  const [selectedGraphNode, setSelectedGraphNode] = useState<string | null>(null);
  const graphRef = useRef<ForceGraphHandle | null>(null);
  const iconNames = useMemo(getIconNames, []);
  const sampleResources = useMemo(() => {
    const requested = Number(new URLSearchParams(window.location.search).get("rows"));
    return Number.isInteger(requested) && requested > resources.length && requested <= 5_000
      ? Array.from({ length: requested }, (_, index): SampleResource => ({
          name: `resource-${index.toString().padStart(4, "0")}`,
          type: index % 2 === 0 ? "Project" : "Container",
          state: "Running",
          stateStyle: null,
          health: "Healthy",
        }))
      : resources;
  }, []);

  const filteredResources = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase();
    return normalizedQuery
      ? sampleResources.filter((resource) => `${resource.name} ${resource.type} ${resource.state}`.toLowerCase().includes(normalizedQuery))
      : sampleResources;
  }, [query, sampleResources]);

  const requestConfirmation = (): void => {
    setConfirmation({
      title: "Restart frontend",
      message: "Restart the frontend resource and its current process?",
      confirmLabel: "Restart",
      onConfirm: () => setLastAction("Restart confirmed"),
    });
  };

  const completeNotification = (message: string): void => {
    setNotificationVisible(false);
    setLastAction(message);
  };

  return (
    <main className="toolkit-playground" data-testid="toolkit-playground">
      <header className="toolkit-playground__header">
        <div>
          <h1>Deck Toolkit</h1>
          <p>Fluent React primitives with Aspire density and tokens</p>
        </div>
        <IconButton
          data-testid="toolkit-theme"
          label={`Use ${theme === "dark" ? "light" : "dark"} theme`}
          icon={theme === "dark" ? <SunIcon size={17} /> : <MoonIcon size={17} />}
          onClick={onToggleTheme}
        />
      </header>

      <div className="toolkit-playground__content">
        <section className="toolkit-section" aria-labelledby="toolkit-page-title">
          <div className="toolkit-section__heading">
            <h2 id="toolkit-page-title">Page composition</h2>
          </div>
          <div className="toolkit-page-sample">
            <Page
              data-testid="toolkit-page-sample"
              aria-labelledby="toolkit-page-sample-title"
            >
              <PageHeader>
                <PageHeading>
                  <PageTitle as="h2" id="toolkit-page-sample-title">Sample resources</PageTitle>
                  <PageSubtitle>3 resources</PageSubtitle>
                </PageHeading>
                <PageActions>
                  <span role="status" aria-live="polite">
                    {pageRefreshCount === 0 ? "Not refreshed" : `Refreshed ${pageRefreshCount} time${pageRefreshCount === 1 ? "" : "s"}`}
                  </span>
                  <Button size="small" onClick={() => setPageRefreshCount((count) => count + 1)}>
                    Refresh sample resources
                  </Button>
                </PageActions>
              </PageHeader>
              <PageToolbar ariaLabel="Sample resource tools">
                <SearchBox value={query} onChange={setQuery} placeholder="Filter sample resources…" />
              </PageToolbar>
              <PageBody data-testid="toolkit-page-body">
                <div className="toolkit-page-sample__resources">
                  {filteredResources.map((resource) => (
                    <div className="toolkit-page-sample__resource" key={resource.name}>
                      <span>{resource.name}</span>
                      <StateDot state={resource.state} stateStyle={resource.stateStyle} health={resource.health} />
                    </div>
                  ))}
                </div>
              </PageBody>
            </Page>
          </div>
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-graph-title">
          <div className="toolkit-section__heading">
            <h2 id="toolkit-graph-title">Force graph</h2>
            <span role="status">{selectedGraphNode ? `Selected ${selectedGraphNode}` : "No graph selection"}</span>
          </div>
          <div className="toolkit-graph-sample">
            <ForceGraph
              ref={graphRef}
              ariaLabel="Sample force graph"
              selectedId={selectedGraphNode}
              onSelect={setSelectedGraphNode}
              nodes={resources.map((resource) => ({
                id: resource.name,
                label: resource.name,
                description: `${resource.type}, ${resource.state}`,
                tone: resource.health === "Healthy" ? "success" : resource.health === "Degraded" ? "warning" : "info",
                icon: <ResourceTypeIcon type={resource.type} size={32} />,
              }))}
              edges={[
                { source: "frontend", target: "catalog-db" },
                { source: "migration", target: "catalog-db" },
              ]}
            />
            <div className="toolkit-graph-sample__controls" role="toolbar" aria-label="Sample graph controls">
              <IconButton label="Sample zoom in" icon={<ZoomInIcon size={16} />} onClick={() => graphRef.current?.zoomIn()} />
              <IconButton label="Sample zoom out" icon={<ZoomOutIcon size={16} />} onClick={() => graphRef.current?.zoomOut()} />
              <IconButton label="Sample reset view" icon={<ResetViewIcon size={16} />} onClick={() => graphRef.current?.reset()} />
            </div>
          </div>
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-actions-title">
          <div className="toolkit-section__heading">
            <h2 id="toolkit-actions-title">Actions</h2>
            <span role="status" aria-live="polite">{lastAction}</span>
          </div>
          <div className="toolkit-row">
            <Button onClick={() => setLastAction("Secondary selected")}>Secondary</Button>
            <Button variant="primary" onClick={() => setLastAction("Primary selected")}>Primary</Button>
            <Button variant="danger" onClick={() => setLastAction("Danger selected")}>Danger</Button>
            <Button variant="ghost" onClick={() => setLastAction("Ghost selected")}>Ghost</Button>
            <Button data-testid="toolkit-confirm" onClick={requestConfirmation}>Confirm command</Button>
            <Button data-testid="toolkit-open-drawer" onClick={() => setDrawerOpen(true)}>Open drawer</Button>
            <Button onClick={() => setDialogOpen(true)}>Open dialog</Button>
            <Button
              onClick={() => setTextViewer({
                title: "Sample structured log",
                value: JSON.stringify({ resource: "frontend", level: "Information", message: "Request completed" }, null, 2),
                format: "json",
              })}
            >
              View sample JSON
            </Button>
            <Button onClick={() => setNotificationVisible(true)}>Show notification</Button>
            <CommandMenu
              ariaLabel="Resource commands"
              triggerContent="Resource commands"
              triggerIcon={<MoreIcon size={15} />}
              entries={[
                {
                  id: "start",
                  label: "Start",
                  description: "Resource is already running",
                  icon: <PlayIcon size={15} />,
                  disabled: true,
                  onSelect: () => setLastAction("Start selected"),
                },
                {
                  id: "restart",
                  label: "Restart",
                  description: "Restart the current process",
                  icon: <RestartIcon size={15} />,
                  onSelect: () => setLastAction("Restart selected"),
                },
                { id: "command-divider", kind: "divider" },
                {
                  id: "stop",
                  label: "Stop",
                  description: "Stop the current process",
                  icon: <StopIcon size={15} />,
                  tone: "danger",
                  onSelect: () => setLastAction("Stop selected"),
                },
              ]}
            />
            <FilterMenu
              ariaLabel="Sample filters"
              icon={<FilterIcon size={16} />}
              active={hiddenSampleTypes.length > 0}
              onClear={() => setHiddenSampleTypes([])}
              groups={[{
                id: "sample-types",
                label: "Type",
                options: ["Project", "Container", "Executable"].map((type) => ({
                  value: type,
                  label: type,
                  checked: !hiddenSampleTypes.includes(type),
                })),
                onChange: (type, checked) => setHiddenSampleTypes((current) => checked
                  ? current.filter((value) => value !== type)
                  : [...current, type]),
              }]}
            />
            <StructuredFilterControl
              filters={structuredFilters}
              fields={["Message", "Resource", "http.request.method", "http.response.status_code"]}
              onChange={setStructuredFilters}
            />
          </div>
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-status-title">
          <div className="toolkit-section__heading">
            <h2 id="toolkit-status-title">Status</h2>
          </div>
          <div className="toolkit-row">
            <Badge>Neutral</Badge>
            <Badge tone="success">Healthy</Badge>
            <Badge tone="info">Starting</Badge>
            <Badge tone="warning">Degraded</Badge>
            <Badge tone="error">Failed</Badge>
            <Badge tone="accent">Selected</Badge>
          </div>
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-icons-title">
          <div className="toolkit-section__heading">
            <h2 id="toolkit-icons-title">Icon mapping</h2>
          </div>
          <div className="toolkit-icon-table-wrap">
            <table className="toolkit-icon-table" data-testid="toolkit-icon-catalog">
              <thead>
                <tr>
                  <th scope="col">Aspire name</th>
                  <th scope="col">Fluent regular</th>
                  <th scope="col">Fluent filled</th>
                </tr>
              </thead>
              <tbody>
                {iconNames.map((name) => {
                  const mapping = iconMappingsByName.get(name.toLowerCase());
                  return (
                    <tr key={name} data-icon-mapping={name}>
                      <td><code>{name}</code></td>
                      <td>
                        <span className="toolkit-icon-component" data-icon-component="regular">
                          <NamedIcon name={name} variant="regular" size={24} aria-label={`${name} regular`} />
                          <code>{mapping?.regularComponent ?? "AppsRegular fallback"}</code>
                        </span>
                      </td>
                      <td>
                        <span className="toolkit-icon-component" data-icon-component="filled">
                          <NamedIcon name={name} variant="filled" size={24} aria-label={`${name} filled`} />
                          <code>{mapping?.filledComponent ?? "AppsRegular fallback"}</code>
                        </span>
                      </td>
                    </tr>
                  );
                })}
                <tr data-icon-mapping="UnknownIntegrationIcon">
                  <td><code>UnknownIntegrationIcon</code></td>
                  <td colSpan={2}>
                    <span className="toolkit-icon-component">
                      <ResourceTypeIcon
                        type="Container"
                        iconName="UnknownIntegrationIcon"
                        size={24}
                        aria-label="Unknown icon container fallback"
                      />
                      <code>Box24Regular resource fallback</code>
                    </span>
                  </td>
                </tr>
                <tr data-icon-mapping="UnknownCommandIcon">
                  <td><code>UnknownCommandIcon</code></td>
                  <td colSpan={2}>
                    <span className="toolkit-icon-component">
                      <NamedIcon
                        name="UnknownCommandIcon"
                        size={24}
                        aria-label="Unknown command icon fallback"
                      />
                      <code>AppsRegular command fallback</code>
                    </span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-inputs-title">
          <div className="toolkit-section__heading">
            <h2 id="toolkit-inputs-title">Inputs</h2>
          </div>
          <div className="toolkit-input-grid">
            <Select
              label="Environment"
              value={environment}
              placeholder="Choose an environment"
              options={[
                { value: "development", label: "Development", group: "Active" },
                { value: "staging", label: "Staging", group: "Active" },
                { value: "production", label: "Production", group: "Active" },
                { value: "retired", label: "Retired", group: "Archived", disabled: true },
              ]}
              onValueChange={setEnvironment}
            />
            <ComboBox
              label="Region"
              value={region}
              allowCustomValue
              placeholder="Choose or enter a region"
              options={[
                { value: "central", label: "Central" },
                { value: "east", label: "East" },
                { value: "west", label: "West" },
              ]}
              onValueChange={setRegion}
            />
            <Checkbox
              label="Include hidden resources"
              checked={includeHidden}
              onCheckedChange={setIncludeHidden}
            />
            <Checkbox label="Select all resources" checked={false} indeterminate />
            <Checkbox label="Unavailable option" checked={false} disabled />
            <Switch
              label="Pause incoming data"
              checked={pauseIncoming}
              onCheckedChange={setPauseIncoming}
            />
            <SecretInput aria-label="Command secret" value={secretInput} placeholder="Enter a secret" onChange={(event) => setSecretInput(event.target.value)} />
            <div className="toolkit-secret-sample">
              <span>API key</span>
              <SecretValue
                value="deck-secret-123"
                revealLabel="Reveal API key"
                hideLabel="Hide API key"
                copyable
                copyLabel="API key"
              />
            </div>
          </div>
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-navigation-title">
          <div className="toolkit-section__heading">
            <h2 id="toolkit-navigation-title">Navigation and disclosure</h2>
          </div>
          <div className="toolkit-disclosure-grid">
            <Tabs
              ariaLabel="Toolkit views"
              selectedId={selectedTab}
              onTabChange={setSelectedTab}
              tabs={[
                {
                  id: "overview",
                  label: "Overview",
                  content: <div className="toolkit-tab-sample">Overview panel</div>,
                },
                {
                  id: "logs",
                  label: (
                    <>
                      <span>Logs</span>
                      <Badge>3</Badge>
                    </>
                  ),
                  content: <div className="toolkit-tab-sample">Logs panel</div>,
                },
              ]}
            />
            <Accordion
              items={[
                {
                  id: "environment",
                  heading: "Environment",
                  count: 2,
                  content: <div className="cell-muted">ASPNETCORE_ENVIRONMENT and HTTP_PORTS</div>,
                },
                {
                  id: "endpoints",
                  heading: "Endpoints",
                  count: 1,
                  content: <div className="cell-muted">https://localhost:7233</div>,
                },
              ]}
              openItems={openAccordionItems}
              onOpenItemsChange={setOpenAccordionItems}
            />
          </div>
          <Divider orientation="horizontal" label="Horizontal divider" />
          <div className="toolkit-inline-sample">
            <span>Resource</span>
            <Divider label="Vertical divider" />
            <span data-testid="toolkit-highlight">
              <Highlighter text="frontend calls FrontEnd API" highlightedText="frontEnd" />
            </span>
          </div>
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-data-title">
          <div className="toolkit-section__heading toolkit-section__heading--data">
            <div>
              <h2 id="toolkit-data-title">Resource data</h2>
              <span>{filteredResources.length} {filteredResources.length === 1 ? "resource" : "resources"}</span>
            </div>
            <SearchBox value={query} onChange={setQuery} placeholder="Filter toolkit resources…" />
          </div>
          <div data-testid="toolkit-table">
            <DataTable
              columns={columns}
              rows={filteredResources}
              rowKey={(resource) => resource.name}
              onRowClick={(resource) => {
                setSelectedResource(resource.name);
                setLastAction(`${resource.name} selected`);
              }}
              isSelected={(resource) => resource.name === selectedResource}
              emptyMessage="No matching resources."
              virtualizeAbove={200}
              virtualHeight={360}
            />
          </div>
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-properties-title">
          <div className="toolkit-section__heading">
            <h2 id="toolkit-properties-title">Properties</h2>
          </div>
          <PropertyGrid
            ariaLabel="Sample properties"
            items={[
              { id: "state", label: "State", value: <StateDot state="Running" stateStyle={null} health="Healthy" /> },
              { id: "resource", label: "Resource", value: "frontend" },
              { id: "trace", label: "Trace ID", value: "0123456789abcdef0123456789abcdef" },
            ]}
          />
          <div data-testid="toolkit-markdown">
            <MarkdownContent markdown={"**Safe Markdown** with `code`, [documentation](https://example.com/docs), and [unsafe](javascript:alert(1)).\n\n- First item\n- Second item"} />
          </div>
          <PropertyExplorer
            ariaLabel="Sample property explorer"
            searchPlaceholder="Filter sample details…"
            defaultOpenItems={["span", "context"]}
            sections={[
              {
                id: "span",
                heading: "Span",
                ariaLabel: "Sample span properties",
                items: [
                  { id: "span-name", label: "Name", value: "GET /catalog" },
                  { id: "span-kind", label: "Kind", value: "Server" },
                  {
                    id: "span-trace",
                    label: "Trace ID",
                    value: "0123456789abcdef0123456789abcdef",
                    valueClassName: "cell-mono",
                  },
                ],
              },
              {
                id: "context",
                heading: "Context",
                ariaLabel: "Sample context properties",
                items: [
                  { id: "scope-name", label: "Source", value: "OpenTelemetry.AspNetCore" },
                  { id: "scope-version", label: "Version", value: "1.9.0" },
                ],
              },
            ]}
          />
        </section>

        <section className="toolkit-section" aria-labelledby="toolkit-empty-title">
          <h2 id="toolkit-empty-title" className="toolkit-visually-hidden">Empty state</h2>
          <EmptyState icon={<ResourcesIcon size={28} />} title="No incidents">
            All monitored resources are within their configured thresholds.
          </EmptyState>
        </section>
      </div>

      {drawerOpen ? (
        <Drawer
          title="frontend"
          subtitle="Project"
          ariaLabel="Toolkit resource details"
          onClose={() => setDrawerOpen(false)}
          footer={<Button variant="primary" onClick={() => setDrawerOpen(false)}>Done</Button>}
        >
          <section className="drawer__section">
            <div className="drawer__section-title">Overview</div>
            <div className="kv">
              <div className="kv__key">State</div>
              <div className="kv__val"><StateDot state="Running" stateStyle={null} health="Healthy" /></div>
              <div className="kv__key">Endpoint</div>
              <div className="kv__val cell-mono">https://localhost:7233</div>
            </div>
          </section>
        </Drawer>
      ) : null}

      <ConfirmDialog request={confirmation} onClose={() => setConfirmation(null)} />
      <Dialog
        open={dialogOpen}
        title="Toolkit dialog"
        onClose={() => setDialogOpen(false)}
        actions={<Button onClick={() => setDialogOpen(false)}>Close dialog</Button>}
      >
        Reusable Fluent modal content.
      </Dialog>
      <TextViewerDialog request={textViewer} onClose={() => setTextViewer(null)} />
      <NotificationStack
        notifications={notificationVisible
          ? [
              {
                id: "toolkit-notification",
                intent: "warning",
                title: "Toolkit notification",
                message: "Review the unresolved sample value.",
                link: {
                  label: "Open documentation",
                  onClick: () => setLastAction("Notification link action"),
                },
                secondaryAction: {
                  label: "Not now",
                  onClick: () => completeNotification("Notification secondary action"),
                },
                primaryAction: {
                  label: "Review",
                  onClick: () => completeNotification("Notification primary action"),
                },
                onDismiss: () => completeNotification("Notification dismissed"),
              },
            ]
          : []}
      />
    </main>
  );
}
