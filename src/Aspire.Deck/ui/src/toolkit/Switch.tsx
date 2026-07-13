import { Switch as FluentSwitch } from "@fluentui/react-components";

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
  return (
    <FluentSwitch
      className={className}
      label={label}
      aria-label={ariaLabel}
      checked={checked}
      disabled={disabled}
      onChange={(_event, data) => onCheckedChange?.(data.checked)}
    />
  );
}
