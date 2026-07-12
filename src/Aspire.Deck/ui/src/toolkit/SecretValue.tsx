import { useState } from "react";
import { IconButton } from "./Button";
import { EyeIcon, EyeOffIcon } from "./Icons";
import { CopyValueButton } from "./CopyValueButton";

export function SecretValue({
  value,
  revealLabel = "Reveal value",
  hideLabel = "Hide value",
  maxMaskLength = 24,
  className,
  copyable = false,
  copyLabel = "value",
}: {
  value: string;
  revealLabel?: string;
  hideLabel?: string;
  maxMaskLength?: number;
  className?: string;
  copyable?: boolean;
  copyLabel?: string;
}) {
  const [revealed, setRevealed] = useState(false);
  const classes = ["deck-secret-value", className].filter(Boolean).join(" ");
  const actionLabel = revealed ? hideLabel : revealLabel;

  return (
    <span className={classes}>
      <span className="secret">
        {revealed ? value : "•".repeat(Math.min(value.length, maxMaskLength))}
      </span>
      <IconButton
        className="reveal-btn"
        label={actionLabel}
        icon={revealed ? <EyeOffIcon size={15} /> : <EyeIcon size={15} />}
        onClick={(event) => {
          event.stopPropagation();
          setRevealed((current) => !current);
        }}
      />
      {copyable && revealed ? <CopyValueButton value={value} label={copyLabel} /> : null}
    </span>
  );
}
