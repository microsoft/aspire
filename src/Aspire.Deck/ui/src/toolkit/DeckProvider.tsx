import type { ReactNode } from "react";

export type DeckTheme = "dark" | "light";

export function DeckProvider({ theme, children }: { theme: DeckTheme; children: ReactNode }) {
  return (
    <div className="deck-provider" data-theme={theme}>
      {children}
    </div>
  );
}
