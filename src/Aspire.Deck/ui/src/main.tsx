import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { useTheme } from "./lib/theme";
import { ToolkitPlayground } from "./playground/ToolkitPlayground";
import { DeckProvider } from "./toolkit";
import "./styles/global.css";

const container = document.getElementById("root");
if (!container) {
  throw new Error("Root container #root not found");
}

function Root() {
  const { theme, themeChoice, setThemeChoice, toggleTheme } = useTheme();
  const view = new URLSearchParams(window.location.search).get("view");

  return (
    <DeckProvider theme={theme}>
      {view === "toolkit" ? (
        <ToolkitPlayground theme={theme} onToggleTheme={toggleTheme} />
      ) : (
        <App theme={theme} themeChoice={themeChoice} onThemeChoiceChange={setThemeChoice} onToggleTheme={toggleTheme} />
      )}
    </DeckProvider>
  );
}

createRoot(container).render(
  <StrictMode>
    <Root />
  </StrictMode>,
);
