import { forwardRef, type ButtonHTMLAttributes, type ReactElement } from "react";
import { Button as ShadcnButton } from "@/components/ui/button";

export type ButtonVariant = "secondary" | "primary" | "danger" | "ghost";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: "small" | "medium";
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = "secondary", size = "medium", className, type = "button", ...props },
  ref,
) {
  const variantClass = variant === "secondary" ? "" : `btn--${variant}`;
  const sizeClass = size === "small" ? "btn--sm" : "";
  const classes = ["btn", variantClass, sizeClass, className].filter(Boolean).join(" ");

  return (
    <ShadcnButton
      ref={ref}
      {...props}
      type={type}
      variant={variant === "primary" ? "default" : variant === "danger" ? "destructive" : variant}
      size={size === "small" ? "sm" : "default"}
      className={classes}
    />
  );
});

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
  return (
    <ShadcnButton
      {...props}
      type={props.type ?? "button"}
      variant="ghost"
      size="icon"
      aria-label={label}
      title={props.title ?? label}
      className={classes}
    >
      {icon}
    </ShadcnButton>
  );
}
