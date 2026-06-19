import { useEffect, useRef, useState } from "react";
import type { AppHostInfo } from "../api/types";
import { selectApphost } from "../api/deck";

// Dropdown that lists the attached AppHosts and switches the active one. Shown in
// the TopBar only when more than one AppHost is attached (a single AppHost needs
// no switcher). Aspire Deck can attach to multiple AppHosts — one per
// `aspire run --deck` — and shows one at a time.
export function AppHostSwitcher({ apphosts }: { apphosts: AppHostInfo[] }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }
    function onDocClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener("mousedown", onDocClick);
    return () => document.removeEventListener("mousedown", onDocClick);
  }, [open]);

  if (apphosts.length <= 1) {
    return null;
  }

  const active = apphosts.find((a) => a.active) ?? apphosts[0];
  if (!active) {
    return null;
  }

  function choose(id: string) {
    setOpen(false);
    if (id !== active!.id) {
      void selectApphost(id);
    }
  }

  return (
    <div className="apphost-switcher" ref={ref}>
      <button
        className="apphost-switcher__button"
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="listbox"
        aria-expanded={open}
        title="Switch AppHost"
      >
        <span className={`pill__dot ${active.state}`} />
        <span className="apphost-switcher__name">{active.name}</span>
        <span className="apphost-switcher__count">{apphosts.length}</span>
        <Chevron />
      </button>

      {open ? (
        <ul className="apphost-switcher__menu" role="listbox">
          {apphosts.map((a) => (
            <li key={a.id} role="option" aria-selected={a.active}>
              <button
                className={`apphost-switcher__item${a.active ? " apphost-switcher__item--active" : ""}`}
                onClick={() => choose(a.id)}
              >
                <span className={`pill__dot ${a.state}`} />
                <span className="apphost-switcher__item-name">{a.name}</span>
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

function Chevron() {
  return (
    <svg width="13" height="13" viewBox="0 0 16 16" fill="none" aria-hidden="true">
      <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
