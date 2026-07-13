import type { ReactNode } from "react";

export function EmptyState({
  icon,
  title,
  children,
}: {
  icon: ReactNode;
  title: string;
  children?: ReactNode;
}) {
  return (
    <div className="empty">
      <div className="empty__icon">{icon}</div>
      <h2 className="empty__title">{title}</h2>
      {children ? <div className="empty__text">{children}</div> : null}
    </div>
  );
}
