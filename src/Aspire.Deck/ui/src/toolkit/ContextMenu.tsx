import { useEffect, useRef, type ReactElement } from "react";
import {
  ContextMenu as ShadcnContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";

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
  const triggerRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    triggerRef.current?.dispatchEvent(new MouseEvent("contextmenu", {
      bubbles: true,
      button: 2,
      clientX: x,
      clientY: y,
    }));
  }, [open, x, y]);

  if (!open) {
    return null;
  }

  return (
    <ShadcnContextMenu onOpenChange={(nextOpen) => {
      if (!nextOpen) onClose();
    }}>
      <ContextMenuTrigger
        ref={triggerRef}
        aria-hidden="true"
        tabIndex={-1}
        style={{ position: "fixed", left: x, top: y, width: 1, height: 1, opacity: 0 }}
      />
      <ContextMenuContent className="context-menu" aria-label={ariaLabel}>
        {entries.map((entry) => (
          <ContextMenuItem
            key={entry.id}
            disabled={entry.disabled}
            onSelect={() => {
              entry.onSelect();
              onClose();
            }}
          >
            {entry.icon}
            <span>{entry.label}</span>
          </ContextMenuItem>
        ))}
      </ContextMenuContent>
    </ShadcnContextMenu>
  );
}
