import { Input } from "@fluentui/react-components";
import { useState } from "react";
import type { TelemetryFilter, TelemetryFilterCondition } from "../lib/telemetryFilters";
import { Button } from "./Button";
import { Checkbox } from "./Checkbox";
import { CommandMenu } from "./CommandMenu";
import { Dialog } from "./Dialog";
import { NamedIcon } from "./Icons";
import { Select } from "./Select";

const CONDITION_OPTIONS: Array<{ value: TelemetryFilterCondition; label: string }> = [
  { value: "equals", label: "Equals" },
  { value: "notEquals", label: "Does not equal" },
  { value: "contains", label: "Contains" },
  { value: "notContains", label: "Does not contain" },
  { value: "gt", label: "Greater than" },
  { value: "gte", label: "Greater than or equal" },
  { value: "lt", label: "Less than" },
  { value: "lte", label: "Less than or equal" },
];

function displayText(filter: TelemetryFilter): string {
  return `${filter.field} ${CONDITION_OPTIONS.find((option) => option.value === filter.condition)?.label.toLocaleLowerCase()} ${filter.value}`;
}

export function StructuredFilterControl({ filters, fields, onChange }: {
  filters: TelemetryFilter[];
  fields: string[];
  onChange: (filters: TelemetryFilter[]) => void;
}) {
  const [editing, setEditing] = useState<TelemetryFilter | null | undefined>(undefined);
  const draft = editing ?? { id: "", field: fields[0] ?? "", condition: "equals" as const, value: "", enabled: true };
  const enabledCount = filters.filter((filter) => filter.enabled).length;
  const updateDraft = (changes: Partial<TelemetryFilter>) => setEditing({ ...draft, ...changes });
  const save = () => {
    if (!draft.field || !draft.value) return;
    const next = draft.id
      ? filters.map((filter) => filter.id === draft.id ? draft : filter)
      : [...filters, { ...draft, id: `filter-${Date.now()}` }];
    onChange(next);
    setEditing(undefined);
  };

  return (
    <div className="structured-filter-control">
      <Button
        size="small"
        variant={filters.length > 0 ? "primary" : "secondary"}
        aria-label="Add filter"
        title="Add filter"
        onClick={() => setEditing(null)}
      >
        <NamedIcon name="FilterAdd" size={16} />
      </Button>
      {filters.length > 0 ? (
        <CommandMenu
          ariaLabel={`Filters, ${enabledCount} enabled`}
          triggerContent={`Filters ${enabledCount}`}
          triggerIcon={<NamedIcon name="Filter" size={16} />}
          entries={[
            ...filters.map((filter) => ({
              id: filter.id,
              label: displayText(filter),
              description: filter.enabled ? "Enabled" : "Disabled",
              icon: <NamedIcon name={filter.enabled ? "Play" : "Pause"} size={16} />,
              onSelect: () => setEditing(filter),
            })),
            { id: "toggle-all", label: enabledCount > 0 ? "Disable all" : "Enable all", icon: <NamedIcon name={enabledCount > 0 ? "Pause" : "Play"} size={16} />, onSelect: () => onChange(filters.map((filter) => ({ ...filter, enabled: enabledCount === 0 }))) },
            { id: "remove-all", label: "Remove all", icon: <NamedIcon name="Delete" size={16} />, tone: "danger" as const, onSelect: () => onChange([]) },
          ]}
        />
      ) : null}
      <Dialog
        open={editing !== undefined}
        title={draft.id ? "Edit filter" : "Add filter"}
        onClose={() => setEditing(undefined)}
        actions={<>
          {draft.id ? <Button variant="danger" onClick={() => { onChange(filters.filter((filter) => filter.id !== draft.id)); setEditing(undefined); }}>Remove</Button> : null}
          <Button onClick={() => setEditing(undefined)}>Cancel</Button>
          <Button variant="primary" disabled={!draft.field || !draft.value} onClick={save}>Apply</Button>
        </>}
      >
        <div className="structured-filter-dialog">
          <Select ariaLabel="Field" value={draft.field} options={fields.map((field) => ({ value: field, label: field }))} onValueChange={(field) => updateDraft({ field })} />
          <Select ariaLabel="Condition" value={draft.condition} options={CONDITION_OPTIONS} onValueChange={(condition) => updateDraft({ condition: condition as TelemetryFilterCondition })} />
          <Input aria-label="Value" value={draft.value} onChange={(_event, data) => updateDraft({ value: data.value })} />
          <Checkbox label="Enabled" ariaLabel="Enabled" checked={draft.enabled} onCheckedChange={(enabled) => updateDraft({ enabled })} />
        </div>
      </Dialog>
    </div>
  );
}
