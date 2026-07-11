import { Fragment, type ReactNode } from "react";

export interface PropertyGridItem {
  id: string;
  label: ReactNode;
  value: ReactNode;
  highlighted?: boolean;
}

export function PropertyGrid({
  items,
  ariaLabel,
  className,
}: {
  items: readonly PropertyGridItem[];
  ariaLabel?: string;
  className?: string;
}) {
  const classes = ["kv", "deck-property-grid", className].filter(Boolean).join(" ");

  return (
    <dl className={classes} role="group" aria-label={ariaLabel}>
      {items.map((item) => (
        <Fragment key={item.id}>
          <dt className="kv__key">{item.label}</dt>
          <dd className={`kv__val ${item.highlighted ? "highlight" : ""}`.trim()}>{item.value}</dd>
        </Fragment>
      ))}
    </dl>
  );
}
