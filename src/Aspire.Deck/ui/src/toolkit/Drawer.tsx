import { useEffect, useId, useState, type CSSProperties, type ReactNode } from "react";
import { IconButton } from "./Button";
import { CloseIcon } from "./Icons";

export interface DrawerProps {
  title: ReactNode;
  subtitle?: ReactNode;
  leading?: ReactNode;
  ariaLabel?: string;
  closeLabel?: string;
  children: ReactNode;
  footer?: ReactNode;
  headerActions?: ReactNode;
  className?: string;
  size?: number;
  onClose: () => void;
}

export function Drawer({
  title,
  subtitle,
  leading,
  ariaLabel,
  closeLabel = "Close details",
  children,
  footer,
  headerActions,
  className,
  size = 560,
  onClose,
}: DrawerProps) {
  const titleId = useId();
  const [panelSize, setPanelSize] = useState(size);
  const [orientation, setOrientation] = useState<"right" | "bottom">("right");

  useEffect(() => setPanelSize(size), [size]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      const target = event.target as HTMLElement | null;
      if (event.metaKey || event.ctrlKey || event.altKey || target?.closest("input, textarea, select, [contenteditable='true']")) return;
      if (event.key === "Escape") {
        onClose();
      } else if (event.shiftKey && event.key.toLowerCase() === "x") {
        event.preventDefault();
        onClose();
      } else if (event.shiftKey && event.key.toLowerCase() === "t") {
        event.preventDefault();
        setOrientation((current) => current === "right" ? "bottom" : "right");
      } else if (event.shiftKey && event.key.toLowerCase() === "r") {
        event.preventDefault();
        setPanelSize(size);
      } else if (event.key === "+") {
        event.preventDefault();
        setPanelSize((current) => Math.min(current + 48, orientation === "right" ? window.innerWidth * 0.8 : window.innerHeight * 0.8));
      } else if (event.key === "-") {
        event.preventDefault();
        setPanelSize((current) => Math.max(240, current - 48));
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose, orientation, size]);

  const panelStyle = { "--drawer-size": `${panelSize}px` } as CSSProperties;

  return (
    <>
      <div className="drawer-overlay" onClick={onClose} />
      <aside
        className={["drawer", `drawer--${orientation}`, className].filter(Boolean).join(" ")}
        style={panelStyle}
        role="dialog"
        aria-modal="true"
        aria-label={ariaLabel}
        aria-labelledby={ariaLabel ? undefined : titleId}
      >
        <div className="drawer__header">
          <div className="drawer__heading">
            {leading}
            <div>
              <div className="drawer__title" id={titleId}>{title}</div>
              {subtitle ? <div className="drawer__subtitle">{subtitle}</div> : null}
            </div>
          </div>
          <div className="drawer__header-actions">
            {headerActions}
            <IconButton label={closeLabel} icon={<CloseIcon size={16} />} onClick={onClose} />
          </div>
        </div>
        <div className="drawer__body">{children}</div>
        {footer ? <div className="drawer__commands">{footer}</div> : null}
      </aside>
    </>
  );
}
