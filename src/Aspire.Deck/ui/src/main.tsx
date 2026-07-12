import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { useTheme } from "./lib/theme";
import { ToolkitPlayground } from "./playground/ToolkitPlayground";
import { DeckProvider } from "./toolkit";
import { AppErrorBoundary } from "./components/AppErrorBoundary";
import { useTimeFormat } from "./lib/timeFormat";
import "./styles/global.css";

const container = document.getElementById("root");
if (!container) {
  throw new Error("Root container #root not found");
}

const renderErrorMarker = "aspire-deck-render-error-triggered";
const triggerRenderError = import.meta.env.DEV
  && new URLSearchParams(window.location.search).get("renderError") === "1"
  && window.sessionStorage.getItem(renderErrorMarker) !== "1";
if (triggerRenderError) {
  window.sessionStorage.setItem(renderErrorMarker, "1");
}

function RenderErrorTrigger(): never {
  throw new Error("Intentional one-shot render error for black-box verification.");
}

function Root() {
  const { theme, themeChoice, setThemeChoice, toggleTheme } = useTheme();
  const [timeFormatChoice, setTimeFormatChoice] = useTimeFormat();
  const view = new URLSearchParams(window.location.search).get("view");

  return (
    <DeckProvider theme={theme}>
      {triggerRenderError ? (
        <RenderErrorTrigger />
      ) : view === "toolkit" ? (
        <ToolkitPlayground theme={theme} onToggleTheme={toggleTheme} />
      ) : (
        <App
          theme={theme}
          themeChoice={themeChoice}
          onThemeChoiceChange={setThemeChoice}
          onToggleTheme={toggleTheme}
          timeFormatChoice={timeFormatChoice}
          onTimeFormatChoiceChange={setTimeFormatChoice}
        />
      )}
    </DeckProvider>
  );
}

createRoot(container).render(
  <StrictMode>
    <AppErrorBoundary>
      <Root />
    </AppErrorBoundary>
  </StrictMode>,
);
