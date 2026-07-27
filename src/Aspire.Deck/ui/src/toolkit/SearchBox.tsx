import { Input } from "@/components/ui/input";
import { SearchIcon } from "./Icons";

export function SearchBox({
  value,
  onChange,
  placeholder = "Search…",
}: {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
}) {
  return (
    <div className="searchbox">
      <SearchIcon size={15} aria-hidden="true" />
      <Input
        aria-label={placeholder}
        value={value}
        placeholder={placeholder}
        spellCheck={false}
        onChange={(event) => onChange(event.currentTarget.value)}
      />
    </div>
  );
}
