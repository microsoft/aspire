import { useId } from "react";
import { Switch as ShadcnSwitch } from "@/components/ui/switch";

export interface SwitchProps {
  label?: string;
  ariaLabel?: string;
  checked: boolean;
  onCheckedChange?: (checked: boolean) => void;
  disabled?: boolean;
  className?: string;
}

export function Switch({
  label,
  ariaLabel,
  checked,
  onCheckedChange,
  disabled,
  className,
}: SwitchProps) {
  const id = useId();
  return (
    <label className={["deck-switch", className].filter(Boolean).join(" ")} htmlFor={id}>
      <ShadcnSwitch
        id={id}
        aria-label={ariaLabel}
        checked={checked}
        disabled={disabled}
        onCheckedChange={onCheckedChange}
      />
      {label ? <span>{label}</span> : null}
    </label>
  );
}
