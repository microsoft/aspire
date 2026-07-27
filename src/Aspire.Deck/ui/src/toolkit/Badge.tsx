import type { ReactNode } from "react";
import { Badge as ShadcnBadge } from "@/components/ui/badge";

export type BadgeTone = "neutral" | "success" | "info" | "warning" | "error" | "accent";

export function Badge({ tone = "neutral", children }: { tone?: BadgeTone; children: ReactNode }) {
  return (
    <ShadcnBadge className={`badge ${tone}`} variant={tone === "error" ? "destructive" : "outline"}>
      {children}
    </ShadcnBadge>
  );
}
