import { RadioGroup as ShadcnRadioGroup, RadioGroupItem } from "@/components/ui/radio-group";

export interface RadioGroupOption<T extends string> {
  value: T;
  label: string;
}

export function RadioGroup<T extends string>({
  ariaLabel,
  value,
  options,
  onValueChange,
  className,
}: {
  ariaLabel: string;
  value: T;
  options: readonly RadioGroupOption<T>[];
  onValueChange: (value: T) => void;
  className?: string;
}) {
  return (
    <ShadcnRadioGroup
      aria-label={ariaLabel}
      value={value}
      onValueChange={(next) => onValueChange(next as T)}
      className={className}
    >
      {options.map((option) => {
        const id = `${ariaLabel}-${option.value}`.replaceAll(/\s+/g, "-").toLocaleLowerCase();
        return (
          <label key={option.value} className="deck-radio" htmlFor={id}>
            <RadioGroupItem id={id} value={option.value} />
            {option.label}
          </label>
        );
      })}
    </ShadcnRadioGroup>
  );
}
