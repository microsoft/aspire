import type { ReactNode } from "react";
import { Badge as FluentBadge, type BadgeProps as FluentBadgeProps } from "@fluentui/react-components";

export type BadgeTone = "neutral" | "success" | "info" | "warning" | "error" | "accent";

const colors: Record<BadgeTone, FluentBadgeProps["color"]> = {
  neutral: "subtle",
  success: "success",
  info: "informative",
  warning: "warning",
  error: "danger",
  accent: "brand",
};

export function Badge({ tone = "neutral", children }: { tone?: BadgeTone; children: ReactNode }) {
  return (
    <FluentBadge className={`badge ${tone}`} appearance="tint" color={colors[tone]}>
      {children}
    </FluentBadge>
  );
}
