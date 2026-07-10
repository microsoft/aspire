import type { ReactNode } from "react";
import { FluentProvider, type Theme, webDarkTheme, webLightTheme } from "@fluentui/react-components";

export type DeckTheme = "dark" | "light";

// Fluent owns component behavior while Deck's CSS custom properties remain the
// source of truth for color, density, and typography in both themes.
const deckThemeOverrides = {
  colorBrandForeground1: "var(--accent)",
  colorBrandForeground2: "var(--accent)",
  colorBrandForeground2Hover: "var(--accent-strong)",
  colorBrandForeground2Pressed: "var(--accent-strong)",
  colorCompoundBrandForeground1: "var(--accent)",
  colorCompoundBrandForeground1Hover: "var(--accent-strong)",
  colorCompoundBrandForeground1Pressed: "var(--accent-strong)",
  colorNeutralForegroundOnBrand: "#ffffff",
  colorBrandBackground: "var(--accent-strong)",
  colorBrandBackgroundHover: "var(--accent)",
  colorBrandBackgroundPressed: "var(--accent-strong)",
  colorBrandBackgroundSelected: "var(--accent-strong)",
  colorCompoundBrandBackground: "var(--accent-strong)",
  colorCompoundBrandBackgroundHover: "var(--accent)",
  colorCompoundBrandBackgroundPressed: "var(--accent-strong)",
  colorBrandStroke1: "var(--accent)",
  colorBrandStroke2: "var(--accent)",
  colorBrandStroke2Hover: "var(--accent-strong)",
  colorBrandStroke2Pressed: "var(--accent-strong)",
  colorCompoundBrandStroke: "var(--accent)",
  colorCompoundBrandStrokeHover: "var(--accent-strong)",
  colorCompoundBrandStrokePressed: "var(--accent-strong)",
  colorNeutralForeground1: "var(--text)",
  colorNeutralForeground1Hover: "var(--text)",
  colorNeutralForeground1Pressed: "var(--text)",
  colorNeutralForeground1Selected: "var(--text)",
  colorNeutralForeground2: "var(--text-secondary)",
  colorNeutralForeground2Hover: "var(--text)",
  colorNeutralForeground2Pressed: "var(--text)",
  colorNeutralForeground2Selected: "var(--text)",
  colorNeutralForeground3: "var(--text-muted)",
  colorNeutralBackground1: "var(--bg-base)",
  colorNeutralBackground1Hover: "var(--bg-hover)",
  colorNeutralBackground1Pressed: "var(--bg-hover)",
  colorNeutralBackground1Selected: "var(--bg-active)",
  colorNeutralBackground2: "var(--bg-surface)",
  colorNeutralBackground2Hover: "var(--bg-hover)",
  colorNeutralBackground2Pressed: "var(--bg-hover)",
  colorNeutralBackground2Selected: "var(--bg-active)",
  colorNeutralBackground3: "var(--bg-surface-2)",
  colorNeutralBackground4: "var(--bg-elevated)",
  colorSubtleBackground: "transparent",
  colorSubtleBackgroundHover: "var(--bg-hover)",
  colorSubtleBackgroundPressed: "var(--bg-hover)",
  colorSubtleBackgroundSelected: "var(--bg-active)",
  colorTransparentBackground: "transparent",
  colorTransparentBackgroundHover: "var(--bg-hover)",
  colorTransparentBackgroundPressed: "var(--bg-hover)",
  colorTransparentBackgroundSelected: "var(--bg-active)",
  colorNeutralStroke1: "var(--border)",
  colorNeutralStroke1Hover: "var(--border-strong)",
  colorNeutralStroke1Pressed: "var(--border-strong)",
  colorNeutralStroke1Selected: "var(--border-focus)",
  colorNeutralStroke2: "var(--border-strong)",
  colorStrokeFocus1: "var(--bg-base)",
  colorStrokeFocus2: "var(--accent)",
  colorStatusSuccessForeground1: "var(--success)",
  colorStatusSuccessBackground1: "var(--success-soft)",
  colorStatusSuccessBorder1: "var(--success)",
  colorStatusWarningForeground1: "var(--warning)",
  colorStatusWarningBackground1: "var(--warning-soft)",
  colorStatusWarningBorder1: "var(--warning)",
  colorStatusDangerForeground1: "var(--error)",
  colorStatusDangerBackground1: "var(--error-soft)",
  colorStatusDangerBorder1: "var(--error)",
  fontFamilyBase: "var(--font)",
  fontFamilyMonospace: "var(--font-mono)",
  borderRadiusSmall: "var(--radius-sm)",
  borderRadiusMedium: "var(--radius-sm)",
  borderRadiusLarge: "var(--radius)",
} satisfies Partial<Theme>;

const deckDarkTheme: Theme = { ...webDarkTheme, ...deckThemeOverrides };
const deckLightTheme: Theme = { ...webLightTheme, ...deckThemeOverrides };

export function DeckProvider({ theme, children }: { theme: DeckTheme; children: ReactNode }) {
  return (
    <FluentProvider className="deck-provider" theme={theme === "dark" ? deckDarkTheme : deckLightTheme}>
      {children}
    </FluentProvider>
  );
}
