import type { ReactElement } from "react";
import { Checkbox as FluentCheckbox } from "@fluentui/react-components";

export interface CheckboxProps {
  checked: boolean;
  onCheckedChange?: (checked: boolean) => void;
  indeterminate?: boolean;
  label?: string | ReactElement;
  ariaLabel?: string;
  title?: string;
  disabled?: boolean;
  className?: string;
}

export function Checkbox({
  checked,
  onCheckedChange,
  indeterminate = false,
  label,
  ariaLabel,
  title,
  disabled,
  className,
}: CheckboxProps) {
  const classes = ["deck-checkbox", disabled ? "deck-checkbox--disabled" : "", className]
    .filter(Boolean)
    .join(" ");

  return (
    <FluentCheckbox
      className={classes}
      checked={indeterminate ? "mixed" : checked}
      label={label}
      aria-label={ariaLabel}
      title={title}
      disabled={disabled}
      onChange={(_event, data) => onCheckedChange?.(data.checked === true)}
    />
  );
}
