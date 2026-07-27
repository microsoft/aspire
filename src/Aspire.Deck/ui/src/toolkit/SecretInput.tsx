import { useState, type InputHTMLAttributes } from "react";
import { IconButton } from "./Button";
import { EyeIcon, EyeOffIcon } from "./Icons";

export function SecretInput({ className, ...props }: Omit<InputHTMLAttributes<HTMLInputElement>, "type">) {
  const [revealed, setRevealed] = useState(false);
  return (
    <span className="deck-secret-input">
      <input
        {...props}
        className={["input", className].filter(Boolean).join(" ")}
        type={revealed ? "text" : "password"}
        autoComplete={props.autoComplete ?? "new-password"}
      />
      <IconButton
        className="deck-secret-input__toggle"
        label={revealed ? "Hide secret" : "Reveal secret"}
        aria-pressed={revealed}
        icon={revealed ? <EyeOffIcon size={15} /> : <EyeIcon size={15} />}
        onClick={() => setRevealed((current) => !current)}
      />
    </span>
  );
}
