import { useEffect, useRef, useState } from "react";
import type { DeckUser } from "../api/types";

function getInitials(name: string): string {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((part) => part[0]?.toLocaleUpperCase() ?? "")
    .join("") || "?";
}

export function UserProfile({ user }: { user: DeckUser }) {
  const [open, setOpen] = useState(false);
  const container = useRef<HTMLDivElement>(null);
  const initials = getInitials(user.name);

  useEffect(() => {
    if (!open) return;
    const close = (event: Event): void => {
      if (event instanceof KeyboardEvent && event.key === "Escape") {
        setOpen(false);
      } else if (event instanceof PointerEvent && !container.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    window.addEventListener("keydown", close);
    window.addEventListener("pointerdown", close);
    return () => {
      window.removeEventListener("keydown", close);
      window.removeEventListener("pointerdown", close);
    };
  }, [open]);

  return (
    <div className="user-profile" ref={container}>
      <button
        className="user-profile__trigger"
        type="button"
        aria-label={`User profile for ${user.name}`}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
      >
        {initials}
      </button>
      {open ? (
        <div className="user-profile__menu" role="menu" aria-label="User profile">
          <div className="user-profile__label">Logged in as</div>
          <div className="user-profile__identity">
            <div className="user-profile__avatar" aria-hidden="true">{initials}</div>
            <div>
              <div className="user-profile__name">{user.name}</div>
              {user.username ? <div className="user-profile__username">{user.username}</div> : null}
            </div>
          </div>
          <form className="user-profile__signout" action="/authentication/logout" method="post">
            <button className="btn btn--ghost" type="submit" role="menuitem">Sign out</button>
          </form>
        </div>
      ) : null}
    </div>
  );
}
