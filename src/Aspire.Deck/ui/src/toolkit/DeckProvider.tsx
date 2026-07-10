import type { ReactNode } from "react";
import { FluentProvider, webDarkTheme, webLightTheme } from "@fluentui/react-components";

export type DeckTheme = "dark" | "light";

export function DeckProvider({ theme, children }: { theme: DeckTheme; children: ReactNode }) {
  return (
    <FluentProvider className="deck-provider" theme={theme === "dark" ? webDarkTheme : webLightTheme}>
      {children}
    </FluentProvider>
  );
}
