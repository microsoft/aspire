import type { MouseEvent, ReactNode } from "react";
import { Badge } from "./Badge";

export interface AccordionItem {
  id: string;
  heading: ReactNode;
  content: ReactNode;
  count?: number;
  disabled?: boolean;
}

export function Accordion({
  items,
  openItems,
  onOpenItemsChange,
  multiple = true,
  collapsible = true,
  className,
}: {
  items: readonly AccordionItem[];
  openItems: readonly string[];
  onOpenItemsChange: (ids: string[]) => void;
  multiple?: boolean;
  collapsible?: boolean;
  className?: string;
}) {
  const classes = ["deck-accordion", className].filter(Boolean).join(" ");

  const toggle = (event: MouseEvent<HTMLElement>, item: AccordionItem): void => {
    // Fluent v9's AccordionPanel currently forwards boolean `inert` under React 18,
    // which React reports as a console error. Native details/summary retains the
    // expected disclosure and keyboard semantics without that runtime warning.
    event.preventDefault();
    if (item.disabled) {
      return;
    }

    const isOpen = openItems.includes(item.id);
    if (isOpen) {
      if (collapsible) {
        onOpenItemsChange(openItems.filter((id) => id !== item.id));
      }
      return;
    }

    onOpenItemsChange(multiple ? [...openItems, item.id] : [item.id]);
  };

  return (
    <div className={classes}>
      {items.map((item) => {
        const isOpen = openItems.includes(item.id);
        return (
          <details key={item.id} className="deck-accordion-item" open={isOpen}>
            <summary
              className="deck-accordion-item__header"
              role="button"
              aria-expanded={isOpen}
              aria-disabled={item.disabled || undefined}
              onClick={(event) => toggle(event, item)}
            >
              <span className="deck-accordion-item__heading">{item.heading}</span>
              {item.count === undefined ? null : (
                <span className="deck-accordion-item__end">
                  <Badge>{item.count}</Badge>
                </span>
              )}
            </summary>
            <div className="deck-accordion-item__body">{item.content}</div>
          </details>
        );
      })}
    </div>
  );
}
