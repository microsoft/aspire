import { useId, type CSSProperties, type ReactNode } from "react";
import { Select as FluentSelect } from "@fluentui/react-components";

export interface SelectOption<T = unknown> {
  value: string;
  label: string;
  data?: T;
  disabled?: boolean;
  title?: string;
}

export interface SelectProps<T = unknown> {
  options: readonly SelectOption<T>[];
  value: string;
  onValueChange?: (value: string, option: SelectOption<T> | undefined) => void;
  label?: ReactNode;
  ariaLabel?: string;
  id?: string;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  fieldClassName?: string;
  style?: CSSProperties;
}

export function Select<T>({
  options,
  value,
  onValueChange,
  label,
  ariaLabel,
  id,
  placeholder,
  disabled,
  className,
  fieldClassName,
  style,
}: SelectProps<T>) {
  const generatedId = useId();
  const controlId = id ?? generatedId;
  const classes = ["select", className].filter(Boolean).join(" ");
  const fieldClasses = ["deck-select-field", fieldClassName].filter(Boolean).join(" ");

  return (
    <div className={fieldClasses}>
      {label ? (
        <label className="deck-select-label" htmlFor={controlId}>
          {label}
        </label>
      ) : null}
      <FluentSelect
        id={controlId}
        className={classes}
        value={value}
        disabled={disabled}
        aria-label={ariaLabel}
        style={style}
        onChange={(event) => {
          const nextValue = event.currentTarget.value;
          onValueChange?.(nextValue, options.find((option) => option.value === nextValue));
        }}
      >
        {placeholder ? (
          <option value="" disabled hidden>
            {placeholder}
          </option>
        ) : null}
        {options.map((option) => (
          <option
            key={option.value}
            value={option.value}
            disabled={option.disabled}
            title={option.title}
          >
            {option.label}
          </option>
        ))}
      </FluentSelect>
    </div>
  );
}
