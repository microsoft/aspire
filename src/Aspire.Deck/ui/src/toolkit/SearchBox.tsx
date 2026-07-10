import { Input } from "@fluentui/react-components";
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
    <Input
      className="searchbox"
      contentBefore={<SearchIcon size={15} />}
      value={value}
      placeholder={placeholder}
      spellCheck={false}
      onChange={(_, data) => onChange(data.value)}
    />
  );
}
