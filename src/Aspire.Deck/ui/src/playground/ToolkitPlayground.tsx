import { useMemo, useState } from "react";
import {
  Badge,
  Button,
  ConfirmDialog,
  DataTable,
  Drawer,
  EmptyState,
  IconButton,
  MoonIcon,
  NotificationStack,
  ResourcesIcon,
  SearchBox,
  StateDot,
  SunIcon,
  type Column,
  type ConfirmRequest,
  type DeckTheme,
} from "../toolkit";

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

const columns: Column<SampleResource>[] = [
  {
    key: "state",
    header: "State",
    render: (resource) => <StateDot state={resource.state} stateStyle={resource.stateStyle} health={resource.health} />,
    width: "240px",
  },
  { key: "name", header: "Name", render: (resource) => <span className="cell-name">{resource.name}</span> },
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
  const [confirmation, setConfirmation] = useState<ConfirmRequest | null>(null);
  const [lastAction, setLastAction] = useState("No action selected");
  const [notificationVisible, setNotificationVisible] = useState(false);

  const filteredResources = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase();
    return normalizedQuery
      ? resources.filter((resource) => `${resource.name} ${resource.type} ${resource.state}`.toLowerCase().includes(normalizedQuery))
      : resources;
  }, [query]);

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
            <Button onClick={() => setNotificationVisible(true)}>Show notification</Button>
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

        <section className="toolkit-section" aria-labelledby="toolkit-data-title">
          <div className="toolkit-section__heading toolkit-section__heading--data">
            <div>
              <h2 id="toolkit-data-title">Resource data</h2>
              <span>{filteredResources.length} {filteredResources.length === 1 ? "resource" : "resources"}</span>
            </div>
            <SearchBox value={query} onChange={setQuery} placeholder="Filter toolkit resources…" />
          </div>
          <div data-testid="toolkit-table">
            <DataTable columns={columns} rows={filteredResources} rowKey={(resource) => resource.name} emptyMessage="No matching resources." />
          </div>
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
