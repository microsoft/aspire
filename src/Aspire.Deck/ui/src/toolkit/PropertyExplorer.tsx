import { useMemo, useState, type ReactNode } from "react";
import { Accordion, type AccordionItem } from "./Accordion";
import { Highlighter } from "./Highlighter";
import { PropertyGrid, type PropertyGridItem } from "./PropertyGrid";
import { SearchBox } from "./SearchBox";

export interface PropertyExplorerItem {
  id: string;
  label: string;
  value: ReactNode;
  searchableText?: string;
  valueClassName?: string;
}

export interface PropertyExplorerSection {
  id: string;
  heading: ReactNode;
  ariaLabel: string;
  items: readonly PropertyExplorerItem[];
  emptyMessage?: string;
}

export function PropertyExplorer({
  ariaLabel,
  sections,
  defaultOpenItems,
  searchPlaceholder = "Filter properties…",
  toolbarStart,
  toolbarEnd,
  className,
}: {
  ariaLabel: string;
  sections: readonly PropertyExplorerSection[];
  defaultOpenItems: readonly string[];
  searchPlaceholder?: string;
  toolbarStart?: ReactNode;
  toolbarEnd?: ReactNode;
  className?: string;
}) {
  const [query, setQuery] = useState("");
  const [openItems, setOpenItems] = useState<string[]>([...defaultOpenItems]);
  const classes = ["deck-property-explorer", className].filter(Boolean).join(" ");

  const accordionItems = useMemo<AccordionItem[]>(() => {
    const normalized = query.trim().toLocaleLowerCase();
    return sections.map((section) => {
      const filtered = normalized.length === 0
        ? [...section.items]
        : section.items.filter((item) => {
            const value = typeof item.value === "string" ? item.value : "";
            return `${item.label} ${item.searchableText ?? value}`.toLocaleLowerCase().includes(normalized);
          });
      const properties: PropertyGridItem[] = filtered.map((item) => ({
        id: item.id,
        label: <Highlighter text={item.label} highlightedText={query} />,
        value: typeof item.value === "string"
          ? <Highlighter text={item.value} highlightedText={query} />
          : item.value,
        valueClassName: item.valueClassName,
      }));

      return {
        id: section.id,
        heading: section.heading,
        count: filtered.length,
        content: properties.length > 0
          ? <PropertyGrid ariaLabel={section.ariaLabel} items={properties} />
          : <div className="deck-property-explorer__empty">{section.emptyMessage ?? "No matching properties."}</div>,
      };
    });
  }, [query, sections]);

  return (
    <div className={classes} role="region" aria-label={ariaLabel}>
      <div className="deck-property-explorer__toolbar" role="toolbar" aria-label={`${ariaLabel} tools`}>
        {toolbarStart}
        <SearchBox value={query} onChange={setQuery} placeholder={searchPlaceholder} />
        {toolbarEnd}
      </div>
      <Accordion
        className="deck-property-explorer__sections"
        items={accordionItems}
        openItems={openItems}
        onOpenItemsChange={setOpenItems}
      />
    </div>
  );
}
