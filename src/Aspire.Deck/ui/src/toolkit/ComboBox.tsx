import { useId, type ReactNode } from "react";
import { Combobox as FluentCombobox, Option } from "@fluentui/react-components";

export interface ComboBoxOption<T = unknown> {
  value: string;
  label: string;
  data?: T;
  disabled?: boolean;
}

export interface ComboBoxProps<T = unknown> {
  options: readonly ComboBoxOption<T>[];
  value: string;
  onValueChange?: (value: string, option: ComboBoxOption<T> | undefined) => void;
  label?: ReactNode;
  ariaLabel?: string;
  id?: string;
  placeholder?: string;
  disabled?: boolean;
  allowCustomValue?: boolean;
  className?: string;
  fieldClassName?: string;
}

export function ComboBox<T>({
  options,
  value,
  onValueChange,
  label,
  ariaLabel,
  id,
  placeholder,
  disabled,
  allowCustomValue = false,
  className,
  fieldClassName,
}: ComboBoxProps<T>) {
  const generatedId = useId();
  const controlId = id ?? generatedId;
  const selected = options.find((option) => option.value === value);
  const classes = ["deck-combobox", className].filter(Boolean).join(" ");
  const fieldClasses = ["deck-combobox-field", fieldClassName].filter(Boolean).join(" ");

  return (
    <div className={fieldClasses}>
      {label ? <label className="deck-select-label" htmlFor={controlId}>{label}</label> : null}
      <FluentCombobox
        id={controlId}
        className={classes}
        aria-label={ariaLabel}
        placeholder={placeholder}
        disabled={disabled}
        freeform={allowCustomValue}
        value={selected?.label ?? value}
        selectedOptions={selected ? [selected.value] : []}
        onChange={(event) => {
          if (allowCustomValue) {
            const nextValue = event.currentTarget.value;
            onValueChange?.(nextValue, options.find((option) => option.label === nextValue));
          }
        }}
        onOptionSelect={(_event, data) => {
          if (data.optionValue === undefined) {
            return;
          }
          const nextValue = data.optionValue;
          onValueChange?.(nextValue, options.find((option) => option.value === nextValue));
        }}
      >
        {options.map((option) => (
          <Option key={option.value} value={option.value} text={option.label} disabled={option.disabled}>
            {option.label}
          </Option>
        ))}
      </FluentCombobox>
    </div>
  );
}
