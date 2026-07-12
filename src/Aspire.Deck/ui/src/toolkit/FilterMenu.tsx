import { Popover, PopoverSurface, PopoverTrigger } from "@fluentui/react-components";
import { useState, type ReactElement } from "react";
import { Button } from "./Button";
import { Checkbox } from "./Checkbox";

export interface FilterMenuOption {
  value: string;
  label: string;
  checked: boolean;
}

export interface FilterMenuGroup {
  id: string;
  label: string;
  options: readonly FilterMenuOption[];
  onChange: (value: string, checked: boolean) => void;
}

export function FilterMenu({
  ariaLabel,
  icon,
  active = false,
  groups,
  onClear,
}: {
  ariaLabel: string;
  icon: ReactElement;
  active?: boolean;
  groups: readonly FilterMenuGroup[];
  onClear: () => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <Popover open={open} onOpenChange={(_event, data) => setOpen(data.open)} positioning="below-start" trapFocus>
      <PopoverTrigger disableButtonEnhancement>
        <Button
          size="small"
          aria-label={ariaLabel}
          aria-pressed={active}
          className={active ? "filter-menu__trigger filter-menu__trigger--active" : "filter-menu__trigger"}
        >
          {icon}
          Filters
        </Button>
      </PopoverTrigger>
      <PopoverSurface className="filter-menu" aria-label={ariaLabel}>
        <div className="filter-menu__header">
          <strong>Filter resources</strong>
          <Button size="small" variant="ghost" disabled={!active} onClick={onClear}>Clear</Button>
          <Button size="small" onClick={() => setOpen(false)}>Done</Button>
        </div>
        {groups.map((group) => (
          <fieldset className="filter-menu__group" key={group.id}>
            <legend>{group.label}</legend>
            {group.options.map((option) => (
              <Checkbox
                key={option.value}
                checked={option.checked}
                label={option.label || "No value"}
                onCheckedChange={(checked) => group.onChange(option.value, checked)}
              />
            ))}
          </fieldset>
        ))}
      </PopoverSurface>
    </Popover>
  );
}
