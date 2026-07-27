import { useEffect, useRef, type ReactElement } from "react";

export interface ContextMenuEntry {
  id: string;
  label: string;
  icon?: ReactElement;
  disabled?: boolean;
  onSelect: () => void;
}

export function ContextMenu({
  open,
  x,
  y,
  ariaLabel,
  entries,
  onClose,
}: {
  open: boolean;
  x: number;
  y: number;
  ariaLabel: string;
  entries: readonly ContextMenuEntry[];
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const closeOnPointer = (event: PointerEvent): void => {
      if (event.target instanceof Node && !ref.current?.contains(event.target)) onClose();
    };
    const closeOnEscape = (event: KeyboardEvent): void => {
      if (event.key === "Escape") onClose();
    };
    document.addEventListener("pointerdown", closeOnPointer);
    document.addEventListener("keydown", closeOnEscape);
    window.setTimeout(() => ref.current?.querySelector<HTMLButtonElement>("button:not(:disabled)")?.focus(), 0);
    return () => {
      document.removeEventListener("pointerdown", closeOnPointer);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, [onClose, open]);

  if (!open) return null;
  return (
    <div
      ref={ref}
      className="context-menu"
      role="menu"
      aria-label={ariaLabel}
      style={{ left: `min(${x}px, calc(100vw - 220px))`, top: `min(${y}px, calc(100vh - ${Math.max(entries.length * 38 + 16, 54)}px))` }}
    >
      {entries.map((entry) => (
        <button
          key={entry.id}
          type="button"
          role="menuitem"
          disabled={entry.disabled}
          onClick={() => { entry.onSelect(); onClose(); }}
        >
          {entry.icon}<span>{entry.label}</span>
        </button>
      ))}
    </div>
  );
}
