import { useEffect, useRef, useState } from "react";
import type { ManageDataRequest, ManageDataResource, ManageDataType } from "../api/types";
import { exportManageData, getManageData, importManageData, removeManageData } from "../api/deck";
import { Button, Checkbox, Drawer, NamedIcon } from "../toolkit";

const TYPE_LABELS: Record<ManageDataType, string> = {
  ResourceDetails: "Resource details",
  ConsoleLogs: "Console logs",
  StructuredLogs: "Structured logs",
  Traces: "Traces",
  Metrics: "Metrics",
  Resource: "Resource",
};

function selectionKey(resourceName: string, dataType: ManageDataType): string {
  return `${resourceName}\u0000${dataType}`;
}

export function ManageDataDrawer({ onClose }: { onClose: () => void }) {
  const [resources, setResources] = useState<ManageDataResource[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [isImportEnabled, setIsImportEnabled] = useState(false);
  const [busy, setBusy] = useState<"export" | "import" | "remove" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  const load = async (selectEverything: boolean): Promise<void> => {
    const response = await getManageData();
    setResources(response.resources);
    setIsImportEnabled(response.isImportEnabled);
    setExpanded((current) => current.size > 0 ? current : new Set(response.resources.map((resource) => resource.name)));
    if (selectEverything) {
      setSelected(new Set(response.resources.flatMap((resource) => resource.dataTypes.map((type) => selectionKey(resource.name, type)))));
    }
  };

  useEffect(() => {
    void load(true).catch((reason) => setError(reason instanceof Error ? reason.message : String(reason)));
  }, []);

  const request = (): ManageDataRequest => ({
    resources: resources.flatMap((resource) => {
      const dataTypes = resource.dataTypes.filter((type) => selected.has(selectionKey(resource.name, type)));
      if (dataTypes.length === 0) return [];
      return [{
        resourceName: resource.name,
        dataTypes: dataTypes.length === resource.dataTypes.length ? [...dataTypes, "Resource"] : dataTypes,
      }];
    }),
  });
  const selectedCount = selected.size;
  const allCount = resources.reduce((count, resource) => count + resource.dataTypes.length, 0);

  const setResourceSelected = (resource: ManageDataResource, checked: boolean): void => {
    setSelected((current) => {
      const next = new Set(current);
      resource.dataTypes.forEach((type) => checked ? next.add(selectionKey(resource.name, type)) : next.delete(selectionKey(resource.name, type)));
      return next;
    });
  };

  const run = async (kind: "export" | "import" | "remove", action: () => Promise<void>): Promise<void> => {
    setBusy(kind);
    setError(null);
    try {
      await action();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusy(null);
    }
  };

  const footer = (
    <div className="manage-data__footer">
      <div className="manage-data__selection-count">{selectedCount} selected</div>
      <Button
        disabled={selectedCount === 0 || busy !== null}
        onClick={() => void run("export", async () => {
          const result = await exportManageData(request());
          const url = URL.createObjectURL(result.blob);
          const anchor = document.createElement("a");
          anchor.href = url;
          anchor.download = result.fileName;
          anchor.click();
          URL.revokeObjectURL(url);
        })}
      >
        <NamedIcon name="Download" size={16} /> Export
      </Button>
      <Button
        variant="danger"
        disabled={selectedCount === 0 || busy !== null}
        onClick={() => void run("remove", async () => {
          await removeManageData(request());
          await load(false);
        })}
      >
        <NamedIcon name="Delete" size={16} /> Remove
      </Button>
      {isImportEnabled ? (
        <>
          <input
            ref={fileInput}
            className="manage-data__file"
            type="file"
            accept=".zip,.json"
            aria-label="Import telemetry file"
            onChange={(event) => {
              const file = event.currentTarget.files?.[0];
              if (!file) return;
              const input = event.currentTarget;
              void run("import", async () => {
                await importManageData(file);
                await load(false);
              }).finally(() => {
                input.value = "";
              });
            }}
          />
          <Button disabled={busy !== null} onClick={() => fileInput.current?.click()}>
            <NamedIcon name="ArrowUpload" size={16} /> Import
          </Button>
        </>
      ) : null}
    </div>
  );

  return (
    <Drawer title="Manage data" closeLabel="Close manage data" onClose={onClose} footer={footer}>
      <div className="manage-data">
        <Checkbox
          label="Name"
          ariaLabel="Select all data"
          checked={allCount > 0 && selectedCount === allCount}
          indeterminate={selectedCount > 0 && selectedCount < allCount}
          disabled={allCount === 0}
          onCheckedChange={(checked) => setSelected(checked
            ? new Set(resources.flatMap((resource) => resource.dataTypes.map((type) => selectionKey(resource.name, type))))
            : new Set())}
        />
        {resources.length === 0 ? <div className="manage-data__empty">No resources have data.</div> : null}
        {resources.map((resource) => {
          const resourceSelected = resource.dataTypes.filter((type) => selected.has(selectionKey(resource.name, type))).length;
          const isExpanded = expanded.has(resource.name);
          return (
            <div className="manage-data__resource" key={resource.name}>
              <div className="manage-data__resource-row">
                <button
                  className="icon-btn icon-btn--sm"
                  type="button"
                  aria-label={`${isExpanded ? "Collapse" : "Expand"} ${resource.displayName}`}
                  onClick={() => setExpanded((current) => {
                    const next = new Set(current);
                    isExpanded ? next.delete(resource.name) : next.add(resource.name);
                    return next;
                  })}
                >
                  <NamedIcon name={isExpanded ? "ChevronDown" : "ChevronRight"} size={14} />
                </button>
                <Checkbox
                  label={resource.displayName}
                  ariaLabel={`Select all data for ${resource.displayName}`}
                  checked={resourceSelected === resource.dataTypes.length}
                  indeterminate={resourceSelected > 0 && resourceSelected < resource.dataTypes.length}
                  onCheckedChange={(checked) => setResourceSelected(resource, checked)}
                />
              </div>
              {isExpanded ? resource.dataTypes.map((type) => (
                <div className="manage-data__type" key={type}>
                  <Checkbox
                    label={TYPE_LABELS[type]}
                    ariaLabel={`Select ${TYPE_LABELS[type]} for ${resource.displayName}`}
                    checked={selected.has(selectionKey(resource.name, type))}
                    onCheckedChange={(checked) => setSelected((current) => {
                      const next = new Set(current);
                      checked ? next.add(selectionKey(resource.name, type)) : next.delete(selectionKey(resource.name, type));
                      return next;
                    })}
                  />
                </div>
              )) : null}
            </div>
          );
        })}
        {error ? <div className="manage-data__error" role="alert">{error}</div> : null}
      </div>
    </Drawer>
  );
}
