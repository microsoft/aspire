import { useId, type ReactElement } from "react";
import { Checkbox as ShadcnCheckbox } from "@/components/ui/checkbox";

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
  const id = useId();
  const classes = ["deck-checkbox", disabled ? "deck-checkbox--disabled" : "", className]
    .filter(Boolean)
    .join(" ");

  return (
    <label className={classes} htmlFor={id} title={title}>
      <ShadcnCheckbox
        id={id}
        checked={indeterminate ? "indeterminate" : checked}
        aria-label={ariaLabel}
        disabled={disabled}
        onCheckedChange={(value) => onCheckedChange?.(value === true)}
      />
      {label ? <span>{label}</span> : null}
    </label>
  );
}
