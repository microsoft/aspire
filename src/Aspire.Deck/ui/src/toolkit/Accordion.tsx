import type { ReactNode } from "react";
import {
  Accordion as ShadcnAccordion,
  AccordionContent,
  AccordionItem as ShadcnAccordionItem,
  AccordionTrigger,
} from "@/components/ui/accordion";
import { Badge } from "./Badge";

export interface AccordionItem {
  id: string;
  heading: ReactNode;
  content: ReactNode;
  count?: ReactNode;
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
  const content = items.map((item) => (
    <ShadcnAccordionItem key={item.id} value={item.id} className="deck-accordion-item">
      <AccordionTrigger className="deck-accordion-item__header" disabled={item.disabled}>
        <span className="deck-accordion-item__heading">{item.heading}</span>
        {item.count === undefined ? null : (
          <span className="deck-accordion-item__end">
            <Badge>{item.count}</Badge>
          </span>
        )}
      </AccordionTrigger>
      <AccordionContent className="deck-accordion-item__body">{item.content}</AccordionContent>
    </ShadcnAccordionItem>
  ));

  const classes = ["deck-accordion", className].filter(Boolean).join(" ");
  return multiple ? (
    <ShadcnAccordion type="multiple" value={[...openItems]} onValueChange={onOpenItemsChange} className={classes}>
      {content}
    </ShadcnAccordion>
  ) : (
    <ShadcnAccordion
      type="single"
      value={openItems[0] ?? ""}
      collapsible={collapsible}
      onValueChange={(id) => onOpenItemsChange(id ? [id] : [])}
      className={classes}
    >
      {content}
    </ShadcnAccordion>
  );
}
