import { useRef, type KeyboardEvent, type ReactElement, type ReactNode } from "react";

export interface TabItem {
  id: string;
  label: ReactNode;
  icon?: ReactElement;
  content?: ReactNode;
  disabled?: boolean;
}

export function Tabs({
  tabs,
  selectedId,
  onTabChange,
  ariaLabel,
  className,
}: {
  tabs: readonly TabItem[];
  selectedId: string;
  onTabChange: (id: string) => void;
  ariaLabel: string;
  className?: string;
}) {
  const classes = ["deck-tabs-host", className].filter(Boolean).join(" ");
  const tabRefs = useRef<Array<HTMLButtonElement | null>>([]);

  const focusTab = (startIndex: number, direction: 1 | -1): void => {
    for (let offset = 1; offset <= tabs.length; offset++) {
      const index = (startIndex + direction * offset + tabs.length) % tabs.length;
      if (!tabs[index]?.disabled) {
        tabRefs.current[index]?.focus();
        return;
      }
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number): void => {
    switch (event.key) {
      case "ArrowRight":
        event.preventDefault();
        focusTab(index, 1);
        break;
      case "ArrowLeft":
        event.preventDefault();
        focusTab(index, -1);
        break;
      case "Home":
        event.preventDefault();
        tabRefs.current[tabs.findIndex((tab) => !tab.disabled)]?.focus();
        break;
      case "End":
        event.preventDefault();
        for (let endIndex = tabs.length - 1; endIndex >= 0; endIndex--) {
          if (!tabs[endIndex]?.disabled) {
            tabRefs.current[endIndex]?.focus();
            break;
          }
        }
        break;
    }
  };

  return (
    <div className={classes}>
      <div className="deck-tabs" role="tablist" aria-label={ariaLabel}>
        {tabs.map((tab, index) => {
          const active = tab.id === selectedId;
          return (
            <button
              key={tab.id}
              ref={(element) => {
                tabRefs.current[index] = element;
              }}
              type="button"
              role="tab"
              id={`deck-tab-${tab.id}`}
              aria-selected={active}
              aria-controls={tab.content === undefined ? undefined : `deck-tab-panel-${tab.id}`}
              tabIndex={active ? 0 : -1}
              disabled={tab.disabled}
              className={`deck-tab ${active ? "deck-tab--active" : ""}`}
              onClick={() => onTabChange(tab.id)}
              onKeyDown={(event) => handleKeyDown(event, index)}
            >
              {tab.icon}
              {tab.label}
            </button>
          );
        })}
      </div>
      {tabs.map((tab) =>
        tab.content === undefined ? null : (
          <div
            key={tab.id}
            id={`deck-tab-panel-${tab.id}`}
            role="tabpanel"
            className="deck-tab-panel"
            aria-labelledby={`deck-tab-${tab.id}`}
            hidden={tab.id !== selectedId}
          >
            {tab.content}
          </div>
        ),
      )}
    </div>
  );
}
