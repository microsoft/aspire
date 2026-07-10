import type { ButtonHTMLAttributes, ReactElement } from "react";
import { Button as FluentButton } from "@fluentui/react-components";

export type ButtonVariant = "secondary" | "primary" | "danger" | "ghost";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: "small" | "medium";
}

export function Button({ variant = "secondary", size = "medium", className, type = "button", ...props }: ButtonProps) {
  const variantClass = variant === "secondary" ? "" : `btn--${variant}`;
  const sizeClass = size === "small" ? "btn--sm" : "";
  const classes = ["btn", variantClass, sizeClass, className].filter(Boolean).join(" ");

  return (
    <FluentButton
      {...props}
      type={type}
      appearance={variant === "primary" ? "primary" : variant === "ghost" ? "subtle" : "secondary"}
      size={size}
      className={classes}
    />
  );
}

export function IconButton({
  label,
  icon,
  className,
  ...props
}: Omit<ButtonHTMLAttributes<HTMLButtonElement>, "aria-label"> & {
  label: string;
  icon: ReactElement;
}) {
  const classes = ["icon-btn", className].filter(Boolean).join(" ");
  return <FluentButton {...props} type={props.type ?? "button"} appearance="subtle" aria-label={label} title={props.title ?? label} icon={icon} className={classes} />;
}
