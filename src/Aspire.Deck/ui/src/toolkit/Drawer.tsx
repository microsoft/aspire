import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetTitle,
} from "@/components/ui/sheet";
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
  showCloseButton?: boolean;
  intent?: string;
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
  showCloseButton = true,
  intent,
  className,
  size = 560,
  onClose,
}: DrawerProps) {
  const [panelSize, setPanelSize] = useState(size);
  const [orientation, setOrientation] = useState<"right" | "bottom">("right");
  const contentRef = useRef<HTMLDivElement>(null);
  const returnFocusRef = useRef(document.activeElement instanceof HTMLElement ? document.activeElement : null);

  useEffect(() => setPanelSize(size), [size]);

  useEffect(() => () => {
    const returnFocus = returnFocusRef.current;
    requestAnimationFrame(() => returnFocus?.focus());
  }, []);

  useEffect(() => {
    requestAnimationFrame(() => {
      contentRef.current?.querySelector<HTMLElement>(
        "button:not(:disabled), a[href], input:not(:disabled), textarea:not(:disabled), select:not(:disabled), [tabindex]:not([tabindex='-1'])",
      )?.focus();
    });
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      const target = event.target as HTMLElement | null;
      if (event.metaKey || event.ctrlKey || event.altKey) return;
      if (event.key === "Tab") {
        const focusable = Array.from(contentRef.current?.querySelectorAll<HTMLElement>(
          "button:not(:disabled), a[href], input:not(:disabled), textarea:not(:disabled), select:not(:disabled), [tabindex]:not([tabindex='-1'])",
        ) ?? []);
        if (focusable.length === 0) return;
        const currentIndex = focusable.indexOf(document.activeElement as HTMLElement);
        const nextIndex = event.shiftKey
          ? (currentIndex <= 0 ? focusable.length - 1 : currentIndex - 1)
          : (currentIndex >= focusable.length - 1 ? 0 : currentIndex + 1);
        event.preventDefault();
        focusable[nextIndex]?.focus();
      } else if (target?.closest("input, textarea, select, [contenteditable='true']")) {
        return;
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
    <Sheet open modal={false} onOpenChange={(open) => {
      if (!open) onClose();
    }}>
      <SheetContent
        ref={contentRef}
        side={orientation === "right" ? "right" : "bottom"}
        showOverlay={false}
        className={["drawer", `drawer--${orientation}`, className].filter(Boolean).join(" ")}
        style={panelStyle}
        aria-label={ariaLabel}
        data-intent={intent}
        onInteractOutside={(event) => event.preventDefault()}
      >
        <div className="drawer__header">
          <div className="drawer__heading">
            {leading}
            <div>
              <SheetTitle className="drawer__title">
                {ariaLabel ? <span className="sr-only">{ariaLabel}</span> : null}
                <span aria-hidden={ariaLabel ? "true" : undefined}>{title}</span>
              </SheetTitle>
              {subtitle ? <SheetDescription className="drawer__subtitle">{subtitle}</SheetDescription> : null}
            </div>
          </div>
          <div className="drawer__header-actions">
            {headerActions}
            {showCloseButton ? <IconButton label={closeLabel} icon={<CloseIcon size={16} />} onClick={onClose} /> : null}
          </div>
        </div>
        <div className="drawer__body">{children}</div>
        {footer ? <div className="drawer__commands">{footer}</div> : null}
      </SheetContent>
    </Sheet>
  );
}
