import { useCallback, useEffect, useLayoutEffect, useState } from "react";

export type Theme = "dark" | "light";
export type ThemeChoice = Theme | "system";

const STORAGE_KEY = "aspire-deck-theme";

function readInitialTheme(): ThemeChoice {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === "dark" || stored === "light" || stored === "system") {
      return stored;
    }
  } catch {
    // Ignore storage access errors (e.g. private mode) and fall back to default.
  }
  return "dark";
}

// Apply the document tokens in a layout effect so they change in the same paint as
// FluentProvider. Suppressing transitions for that paint prevents foreground and
// background colors from interpolating independently through unreadable values.
function systemTheme(): Theme {
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export function useTheme(): {
  theme: Theme;
  themeChoice: ThemeChoice;
  setThemeChoice: (choice: ThemeChoice) => void;
  toggleTheme: () => void;
} {
  const [themeChoice, setThemeChoice] = useState<ThemeChoice>(readInitialTheme);
  const [systemPreference, setSystemPreference] = useState<Theme>(systemTheme);
  const theme = themeChoice === "system" ? systemPreference : themeChoice;

  useEffect(() => {
    const media = window.matchMedia?.("(prefers-color-scheme: dark)");
    if (!media) {
      return;
    }
    const update = (): void => setSystemPreference(media.matches ? "dark" : "light");
    media.addEventListener("change", update);
    return () => media.removeEventListener("change", update);
  }, []);

  useLayoutEffect(() => {
    const root = document.documentElement;
    root.setAttribute("data-theme-switching", "");
    root.setAttribute("data-theme", theme);
    const frame = requestAnimationFrame(() => root.removeAttribute("data-theme-switching"));
    return () => cancelAnimationFrame(frame);
  }, [theme]);

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, themeChoice);
    } catch {
      // Ignore persistence failures.
    }
  }, [themeChoice]);

  const toggleTheme = useCallback(() => {
    setThemeChoice(theme === "dark" ? "light" : "dark");
  }, [theme]);

  return { theme, themeChoice, setThemeChoice, toggleTheme };
}
