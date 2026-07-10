import { useMemo, useState } from "react";
import type { Resource, ResourceCommand } from "../api/types";
import { PARAMETER_RESOURCE_TYPE, PARAMETER_VALUE_PROPERTY } from "../api/types";
import { executeCommand } from "../api/deck";
import { useResources } from "../lib/useDeckEvent";
import { DetailsDrawer } from "../components/DetailsDrawer";
import {
  ConfirmDialog,
  DataTable,
  EyeIcon,
  EyeOffIcon,
  ParametersIcon,
  SearchBox,
  StateDot,
  type Column,
  type ConfirmRequest,
} from "../toolkit";

interface Toast {
  message: string;
  tone: "success" | "error";
}

// A parameter is "unset" when its value couldn't be resolved (no value in config,
// user secrets, or a default). Aspire reports this as the ValueMissing state.
function isUnset(resource: Resource): boolean {
  return resource.state === "ValueMissing";
}

function valueProperty(resource: Resource) {
  return resource.properties.find((p) => p.name === PARAMETER_VALUE_PROPERTY) ?? null;
}

export function ParametersPage() {
  const { resources, ready } = useResources();
  const [query, setQuery] = useState("");
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [toast, setToast] = useState<Toast | null>(null);

  const visible = useMemo(() => {
    const list = resources.filter((r) => !r.isHidden && r.resourceType === PARAMETER_RESOURCE_TYPE);
    const trimmed = query.trim().toLowerCase();
    const filtered = trimmed
      ? list.filter(
          (r) =>
            r.displayName.toLowerCase().includes(trimmed) ||
            (r.state ?? "").toLowerCase().includes(trimmed),
        )
      : list;
    return [...filtered].sort((a, b) => a.displayName.localeCompare(b.displayName));
  }, [resources, query]);

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
        setToast({ message: response.message ?? `${command.displayName} ${response.kind}`, tone: "error" });
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
      width: "260px",
      render: (r) => (
        <span className="cell-name">
          <ParametersIcon size={15} className="cell-type-icon" />
          {r.displayName}
        </span>
      ),
    },
    {
      key: "value",
      header: "Value",
      render: (r) => <ValueCell resource={r} />,
    },
  ];

  return (
    <div className="page">
      <div className="page__header">
        <div>
          <div className="page__title">Parameters</div>
          <div className="page__subtitle">
            {ready ? `${visible.length} parameter${visible.length === 1 ? "" : "s"}` : "Loading…"}
          </div>
        </div>
      </div>

      <div className="page__toolbar">
        <SearchBox value={query} onChange={setQuery} placeholder="Filter by name or state…" />
      </div>

      <div className="page__body">
        <DataTable
          columns={columns}
          rows={visible}
          rowKey={(r) => r.name}
          onRowClick={(r) => setSelectedName(r.name)}
          isSelected={(r) => r.name === selectedName}
          emptyMessage={ready ? "This AppHost has no parameters." : "Connecting to resource service…"}
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

// Shows the parameter's value: a muted "Not set" when unresolved, the value when
// plain, or a reveal-on-demand mask when the parameter is a secret.
function ValueCell({ resource }: { resource: Resource }) {
  const [revealed, setRevealed] = useState(false);
  const prop = valueProperty(resource);

  if (isUnset(resource) || !prop || prop.value.length === 0) {
    return <span className="cell-muted">Not set</span>;
  }

  if (!prop.isSensitive) {
    return <span className="param-value">{prop.value}</span>;
  }

  return (
    <span className="param-value param-value--secret">
      <span className="secret">{revealed ? prop.value : "•".repeat(Math.min(prop.value.length, 24))}</span>
      <button
        type="button"
        className="icon-btn"
        title={revealed ? "Hide value" : "Reveal value"}
        aria-label={revealed ? "Hide value" : "Reveal value"}
        onClick={(e) => {
          e.stopPropagation();
          setRevealed((v) => !v);
        }}
      >
        {revealed ? <EyeOffIcon size={14} /> : <EyeIcon size={14} />}
      </button>
    </span>
  );
}
