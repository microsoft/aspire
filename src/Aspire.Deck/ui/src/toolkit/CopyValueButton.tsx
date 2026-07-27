import { useState } from "react";
import { IconButton } from "./Button";
import { CopyIcon } from "./Icons";

export function CopyValueButton({
  value,
  label = "value",
  className,
}: {
  value: string;
  label?: string;
  className?: string;
}) {
  const [status, setStatus] = useState("");

  const copy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(value);
      setStatus(`${label} copied`);
    } catch {
      setStatus(`Could not copy ${label}`);
    }
  };

  return (
    <span className={["copy-value", className].filter(Boolean).join(" ")}>
      <IconButton
        className="copy-value__button"
        label={`Copy ${label}`}
        icon={<CopyIcon size={15} />}
        onClick={(event) => { event.stopPropagation(); void copy(); }}
      />
      {status ? <span className="copy-value__status" role="status" aria-live="polite">{status}</span> : null}
    </span>
  );
}
