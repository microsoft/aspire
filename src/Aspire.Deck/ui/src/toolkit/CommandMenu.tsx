import {
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactElement,
  type ReactNode,
} from "react";
import { Button, type ButtonVariant } from "./Button";

export interface CommandMenuAction {
  id: string;
  label: string;
  description?: string;
  icon?: ReactElement;
  disabled?: boolean;
  tone?: "default" | "danger";
  onSelect: () => void;
}

export interface CommandMenuDivider {
  id: string;
  kind: "divider";
}

export type CommandMenuEntry = CommandMenuAction | CommandMenuDivider;

export interface CommandMenuProps {
  ariaLabel: string;
  triggerContent?: ReactNode;
  triggerIcon?: ReactElement;
  triggerVariant?: ButtonVariant;
  triggerSize?: "small" | "medium";
  placement?: "below-start" | "below-end" | "above-start" | "above-end";
  entries: readonly CommandMenuEntry[];
}

function isDivider(entry: CommandMenuEntry): entry is CommandMenuDivider {
  return "kind" in entry && entry.kind === "divider";
}

export function CommandMenu({
  ariaLabel,
  triggerContent,
  triggerIcon,
  triggerVariant = "secondary",
  triggerSize = "medium",
  placement = "below-start",
  entries,
}: CommandMenuProps) {
  const menuId = useId();
  const rootRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const itemRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);

  const actionIndexes = (): number[] =>
    entries.flatMap((entry, index) => (isDivider(entry) ? [] : [index]));

  const openAt = (index: number): void => {
    setActiveIndex(index);
    setOpen(true);
  };

  const close = (restoreFocus: boolean): void => {
    setOpen(false);
    if (restoreFocus) {
      window.setTimeout(() => triggerRef.current?.focus(), 0);
    }
  };

  useLayoutEffect(() => {
    if (open && activeIndex >= 0) {
      itemRefs.current[activeIndex]?.focus();
    }
  }, [activeIndex, open]);

  useEffect(() => {
    if (!open) {
      return;
    }

    const onPointerDown = (event: PointerEvent): void => {
      if (event.target instanceof Node && !rootRef.current?.contains(event.target)) {
        setOpen(false);
      }
    };

    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [open]);

  const onTriggerKeyDown = (event: KeyboardEvent<HTMLButtonElement>): void => {
    const indexes = actionIndexes();
    if (indexes.length === 0) {
      return;
    }

    if (event.key === "ArrowDown") {
      event.preventDefault();
      openAt(indexes[0]!);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      openAt(indexes[indexes.length - 1]!);
    }
  };

  const onMenuKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    const indexes = actionIndexes();
    const position = indexes.indexOf(activeIndex);
    let nextIndex: number | undefined;

    if (event.key === "ArrowDown") {
      nextIndex = indexes[(position + 1) % indexes.length];
    } else if (event.key === "ArrowUp") {
      nextIndex = indexes[(position - 1 + indexes.length) % indexes.length];
    } else if (event.key === "Home") {
      nextIndex = indexes[0];
    } else if (event.key === "End") {
      nextIndex = indexes[indexes.length - 1];
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      close(true);
      return;
    } else if (event.key === "Tab") {
      setOpen(false);
      return;
    }

    if (nextIndex !== undefined) {
      event.preventDefault();
      setActiveIndex(nextIndex);
    }
  };

  const select = (entry: CommandMenuAction): void => {
    if (entry.disabled) {
      return;
    }
    entry.onSelect();
    close(true);
  };

  const indexes = actionIndexes();

  return (
    <div className="command-menu-root" ref={rootRef}>
      <Button
        ref={triggerRef}
        variant={triggerVariant}
        size={triggerSize}
        aria-label={ariaLabel}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        className={triggerContent === null ? "icon-btn" : undefined}
        title={triggerContent === null ? ariaLabel : undefined}
        onClick={() => {
          if (open) {
            close(false);
          } else if (indexes.length > 0) {
            openAt(indexes[0]!);
          }
        }}
        onKeyDown={onTriggerKeyDown}
      >
        {triggerIcon}
        {triggerContent === undefined ? ariaLabel : triggerContent}
      </Button>
      {open ? (
        <div
          id={menuId}
          className={`command-menu command-menu--${placement}`}
          role="menu"
          aria-label={ariaLabel}
          onKeyDown={onMenuKeyDown}
        >
          {entries.map((entry, index) => {
            if (isDivider(entry)) {
              return <div key={entry.id} className="command-menu__divider" role="separator" />;
            }

            return (
              <button
                key={entry.id}
                ref={(element) => {
                  itemRefs.current[index] = element;
                }}
                type="button"
                role="menuitem"
                aria-disabled={entry.disabled || undefined}
                tabIndex={index === activeIndex ? 0 : -1}
                className={`command-menu__item ${entry.tone === "danger" ? "command-menu__item--danger" : ""}`.trim()}
                onFocus={() => setActiveIndex(index)}
                onClick={() => select(entry)}
              >
                {entry.icon ? <span className="command-menu__icon">{entry.icon}</span> : null}
                <span className="command-menu__content">
                  <span className="command-menu__label">{entry.label}</span>
                  {entry.description ? (
                    <span className="command-menu__description">{entry.description}</span>
                  ) : null}
                </span>
              </button>
            );
          })}
        </div>
      ) : null}
    </div>
  );
}
