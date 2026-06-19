import type { ReactNode } from "react";

type Tone = "neutral" | "success" | "info" | "warning" | "error" | "accent";

export function Badge({ tone = "neutral", children }: { tone?: Tone; children: ReactNode }) {
  return <span className={`badge ${tone}`}>{children}</span>;
}
