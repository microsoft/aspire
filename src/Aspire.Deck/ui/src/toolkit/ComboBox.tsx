import { useEffect, useId, useMemo, useState, type ReactNode } from "react";
import { Check } from "lucide-react";
import {
  Command,
  CommandEmpty,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Popover, PopoverAnchor, PopoverContent } from "@/components/ui/popover";

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
  ariaInvalid?: boolean;
  ariaDescribedBy?: string;
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
  ariaInvalid,
  ariaDescribedBy,
}: ComboBoxProps<T>) {
  const generatedId = useId();
  const [open, setOpen] = useState(false);
  const controlId = id ?? generatedId;
  const selected = options.find((option) => option.value === value);
  const [inputValue, setInputValue] = useState(selected?.label ?? value);
  const classes = ["deck-combobox", className].filter(Boolean).join(" ");
  const fieldClasses = ["deck-combobox-field", fieldClassName].filter(Boolean).join(" ");
  const filteredOptions = useMemo(() => {
    const query = inputValue.trim().toLocaleLowerCase();
    return query.length === 0 || selected?.label === inputValue
      ? options
      : options.filter((option) => option.label.toLocaleLowerCase().includes(query));
  }, [inputValue, options, selected?.label]);

  useEffect(() => {
    setInputValue(selected?.label ?? value);
  }, [selected?.label, value]);

  return (
    <div className={fieldClasses}>
      {label ? <label className="deck-select-label" htmlFor={controlId}>{label}</label> : null}
      <Popover
        open={open}
        onOpenChange={(nextOpen) => {
          setOpen(nextOpen);
          if (!nextOpen && !allowCustomValue) {
            setInputValue(selected?.label ?? value);
          }
        }}
      >
        <PopoverAnchor asChild>
          <input
            id={controlId}
            role="combobox"
            type="text"
            aria-label={ariaLabel}
            aria-expanded={open}
            aria-autocomplete="list"
            aria-invalid={ariaInvalid || undefined}
            aria-describedby={ariaDescribedBy}
            disabled={disabled}
            className={classes}
            placeholder={placeholder}
            value={inputValue}
            onClick={() => setOpen(true)}
            onFocus={() => setOpen(true)}
            onKeyDown={(event) => {
              if (event.key === "ArrowDown") {
                setOpen(true);
              } else if (event.key === "Escape") {
                setOpen(false);
              }
            }}
            onChange={(event) => {
              const nextValue = event.target.value;
              setInputValue(nextValue);
              setOpen(true);
              if (allowCustomValue) {
                onValueChange?.(
                  nextValue,
                  options.find((option) => option.value === nextValue || option.label === nextValue),
                );
              }
            }}
          />
        </PopoverAnchor>
        <PopoverContent className="deck-combobox__popover" align="start">
          <Command shouldFilter={false}>
            <CommandList>
              {filteredOptions.length === 0 ? <CommandEmpty>No options found.</CommandEmpty> : null}
              {filteredOptions.map((option) => (
                <CommandItem
                  key={option.value}
                  value={option.label}
                  disabled={option.disabled}
                  onSelect={() => {
                    setInputValue(option.label);
                    onValueChange?.(option.value, option);
                    setOpen(false);
                  }}
                >
                  <Check className={selected?.value === option.value ? "visible" : "invisible"} aria-hidden="true" />
                  {option.label}
                </CommandItem>
              ))}
            </CommandList>
          </Command>
        </PopoverContent>
      </Popover>
    </div>
  );
}
