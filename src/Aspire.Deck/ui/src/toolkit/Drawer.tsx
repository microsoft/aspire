import { useEffect, useId, type ReactNode } from "react";
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
  onClose,
}: DrawerProps) {
  const titleId = useId();

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === "Escape") {
        onClose();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  return (
    <>
      <div className="drawer-overlay" onClick={onClose} />
      <aside
        className="drawer"
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
          <IconButton label={closeLabel} icon={<CloseIcon size={16} />} onClick={onClose} />
        </div>
        <div className="drawer__body">{children}</div>
        {footer ? <div className="drawer__commands">{footer}</div> : null}
      </aside>
    </>
  );
}
