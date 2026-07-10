import { useMemo, useState } from "react";
import type { Resource, ResourceCommand } from "../api/types";
import { PARAMETER_RESOURCE_TYPE } from "../api/types";
import { executeCommand, openExternal } from "../api/deck";
import { useResources } from "../lib/useDeckEvent";
import { formatRelativeTime } from "../lib/format";
import { DetailsDrawer } from "../components/DetailsDrawer";
import {
  ConfirmDialog,
  DataTable,
  ExternalIcon,
  ResourceTypeIcon,
  SearchBox,
  StateDot,
  type Column,
  type ConfirmRequest,
} from "../toolkit";

interface Toast {
  message: string;
  tone: "success" | "error";
}

export function ResourcesPage() {
  const { resources, ready } = useResources();
  const [query, setQuery] = useState("");
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [toast, setToast] = useState<Toast | null>(null);

  const visible = useMemo(() => {
    // Parameters have their own page, so they're excluded here.
    const list = resources.filter((r) => !r.isHidden && r.resourceType !== PARAMETER_RESOURCE_TYPE);
    const trimmed = query.trim().toLowerCase();
    const filtered = trimmed
      ? list.filter(
          (r) =>
            r.displayName.toLowerCase().includes(trimmed) ||
            r.resourceType.toLowerCase().includes(trimmed) ||
            (r.state ?? "").toLowerCase().includes(trimmed),
        )
      : list;
    return [...filtered].sort((a, b) => a.displayName.localeCompare(b.displayName));
  }, [resources, query]);

  // Resolve the selected resource from the live list so the drawer reflects updates.
  const selected = useMemo(
    () => resources.find((r) => r.name === selectedName) ?? null,
    [resources, selectedName],
  );

  const runCommand = async (resource: Resource, command: ResourceCommand): Promise<void> => {
    try {
      const response = await executeCommand({
        resourceName: resource.name,
        resourceType: resource.resourceType,
        commandName: command.name,
      });
      if (response.kind === "succeeded") {
        setToast({ message: `${command.displayName} succeeded`, tone: "success" });
      } else {
        setToast({
          message: response.message ?? `${command.displayName} ${response.kind}`,
          tone: "error",
        });
      }
    } catch (err) {
      setToast({ message: `Command failed: ${String(err)}`, tone: "error" });
    }
    window.setTimeout(() => setToast(null), 3200);
  };

  const columns: Column<Resource>[] = [
    {
      key: "state",
      header: "State",
      width: "170px",
      render: (r) => <StateDot state={r.state} stateStyle={r.stateStyle} health={r.health} />,
    },
    {
      key: "name",
      header: "Name",
      render: (r) => (
        <span className="cell-name">
          <ResourceTypeIcon type={r.resourceType} size={16} className="cell-type-icon" />
          {r.displayName}
        </span>
      ),
    },
    {
      key: "type",
      header: "Type",
      width: "120px",
      render: (r) => <span className="cell-muted">{r.resourceType}</span>,
    },
    {
      key: "endpoints",
      header: "Endpoints",
      render: (r) => {
        const urls = r.urls.filter((u) => !u.isInactive && !u.isInternal);
        if (urls.length === 0) {
          return <span className="cell-muted">—</span>;
        }
        return (
          <span className="url-list">
            {urls.map((url) => (
              <a
                key={url.url}
                className="url-chip"
                href={url.url}
                title={url.url}
                onClick={(e) => {
                  e.preventDefault();
                  e.stopPropagation();
                  void openExternal(url.url);
                }}
              >
                <ExternalIcon size={11} />
                {url.url}
              </a>
            ))}
          </span>
        );
      },
    },
    {
      key: "started",
      header: "Started",
      width: "120px",
      render: (r) => <span className="cell-muted">{formatRelativeTime(r.startedAt)}</span>,
    },
  ];

  return (
    <div className="page">
      <div className="page__header">
        <div>
          <div className="page__title">Resources</div>
          <div className="page__subtitle">
            {ready ? `${visible.length} resource${visible.length === 1 ? "" : "s"}` : "Loading…"}
          </div>
        </div>
      </div>

      <div className="page__toolbar">
        <SearchBox value={query} onChange={setQuery} placeholder="Filter by name, type or state…" />
      </div>

      <div className="page__body">
        <DataTable
          columns={columns}
          rows={visible}
          rowKey={(r) => r.name}
          onRowClick={(r) => setSelectedName(r.name)}
          isSelected={(r) => r.name === selectedName}
          emptyMessage={ready ? "No resources match your filter." : "Connecting to resource service…"}
        />
      </div>

      {selected ? (
        <DetailsDrawer
          resource={selected}
          onClose={() => setSelectedName(null)}
          onExecuteCommand={(resource, command) => void runCommand(resource, command)}
          requestConfirm={setConfirm}
        />
      ) : null}

      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />

      {toast ? (
        <div className="toast" role="status" aria-live="polite">
          <span className={`state__dot ${toast.tone === "success" ? "success" : "error"}`} />
          {toast.message}
        </div>
      ) : null}
    </div>
  );
}
