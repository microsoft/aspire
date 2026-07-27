import { useId, type CSSProperties, type ReactNode } from "react";
import {
  Select as ShadcnSelect,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

export interface SelectOption<T = unknown> {
  value: string;
  label: string;
  group?: string;
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
  const groups = new Map<string | null, SelectOption<T>[]>();
  for (const option of options) {
    const group = option.group ?? null;
    const groupOptions = groups.get(group);
    if (groupOptions) {
      groupOptions.push(option);
    } else {
      groups.set(group, [option]);
    }
  }

  const renderOption = (option: SelectOption<T>) => (
    <SelectItem
      key={option.value}
      value={option.value}
      disabled={option.disabled}
      title={option.title}
    >
      <span data-option-value={option.value}>{option.label}</span>
    </SelectItem>
  );

  return (
    <div className={fieldClasses}>
      {label ? (
        <label className="deck-select-label" htmlFor={controlId}>
          {label}
        </label>
      ) : null}
      <ShadcnSelect
        value={value}
        disabled={disabled}
        onValueChange={(nextValue) => {
          onValueChange?.(nextValue, options.find((option) => option.value === nextValue));
        }}
      >
        <SelectTrigger
          id={controlId}
          className={classes}
          aria-label={ariaLabel}
          data-value={value}
          style={style}
        >
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent>
          {[...groups].map(([group, groupOptions]) => group ? (
            <SelectGroup key={group}>
              <SelectLabel>{group}</SelectLabel>
              {groupOptions.map(renderOption)}
            </SelectGroup>
          ) : groupOptions.map(renderOption))}
        </SelectContent>
      </ShadcnSelect>
    </div>
  );
}
