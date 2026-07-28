import type { ReactElement, ReactNode } from "react";
import {
  Tabs as ShadcnTabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";

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
  return (
    <ShadcnTabs
      value={selectedId}
      onValueChange={onTabChange}
      className={["deck-tabs-host", className].filter(Boolean).join(" ")}
    >
      <TabsList className="deck-tabs" variant="line" aria-label={ariaLabel}>
        {tabs.map((tab) => (
          <TabsTrigger
            key={tab.id}
            value={tab.id}
            id={`deck-tab-${tab.id}`}
            disabled={tab.disabled}
            className="deck-tab"
          >
            {tab.icon}
            {tab.label}
          </TabsTrigger>
        ))}
      </TabsList>
      {tabs.map((tab) =>
        tab.content === undefined ? null : (
          <TabsContent
            key={tab.id}
            value={tab.id}
            id={`deck-tab-panel-${tab.id}`}
            aria-labelledby={`deck-tab-${tab.id}`}
            forceMount
            hidden={tab.id !== selectedId}
            className="deck-tab-panel"
          >
            {tab.content}
          </TabsContent>
        ),
      )}
    </ShadcnTabs>
  );
}
