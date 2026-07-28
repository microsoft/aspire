import { useRef, type ReactElement, type ReactNode } from "react";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
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
  const selected = useRef(false);
  const side = placement.startsWith("above") ? "top" : "bottom";
  const align = placement.endsWith("end") ? "end" : "start";

  return (
    <DropdownMenu modal={false}>
      <div className="command-menu-root">
        <DropdownMenuTrigger asChild>
          <Button
            variant={triggerVariant}
            size={triggerSize}
            aria-label={ariaLabel}
            className={triggerContent === null ? "icon-btn" : undefined}
            title={triggerContent === null ? ariaLabel : undefined}
          >
            {triggerIcon}
            {triggerContent === undefined ? ariaLabel : triggerContent}
          </Button>
        </DropdownMenuTrigger>
      </div>
      <DropdownMenuContent
        className="command-menu"
        side={side}
        align={align}
        aria-label={ariaLabel}
        onCloseAutoFocus={(event) => {
          if (selected.current) {
            event.preventDefault();
            selected.current = false;
          }
        }}
      >
        {entries.map((entry) => {
          if (isDivider(entry)) {
            return <DropdownMenuSeparator key={entry.id} className="command-menu__divider" />;
          }

          return (
            <DropdownMenuItem
              key={entry.id}
              disabled={entry.disabled}
              variant={entry.tone === "danger" ? "destructive" : "default"}
              className={[
                "command-menu__item",
                entry.tone === "danger" ? "command-menu__item--danger" : "",
              ].filter(Boolean).join(" ")}
              onSelect={() => {
                selected.current = true;
                entry.onSelect();
              }}
            >
              {entry.icon ? <span className="command-menu__icon">{entry.icon}</span> : null}
              <span className="command-menu__content">
                <span className="command-menu__label">{entry.label}</span>
                {entry.description ? (
                  <span className="command-menu__description">{entry.description}</span>
                ) : null}
              </span>
            </DropdownMenuItem>
          );
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
