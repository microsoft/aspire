import type { AppHostInfo } from "../api/types";
import { selectApphost } from "../api/deck";
import { Select, SelectContent, SelectItem, SelectTrigger } from "@/components/ui/select";

// Dropdown that lists the attached AppHosts and switches the active one. Shown in
// the TopBar only when more than one AppHost is attached (a single AppHost needs
// no switcher). Aspire Deck can attach to multiple AppHosts — one per
// `aspire run --deck` — and shows one at a time.
export function AppHostSwitcher({ apphosts }: { apphosts: AppHostInfo[] }) {
  if (apphosts.length <= 1) {
    return null;
  }

  const active = apphosts.find((a) => a.active) ?? apphosts[0];
  if (!active) {
    return null;
  }

  function choose(id: string) {
    if (id !== active!.id) {
      void selectApphost(id);
    }
  }

  return (
    <div className="apphost-switcher">
      <Select value={active.id} onValueChange={choose}>
        <SelectTrigger className="apphost-switcher__button" title="Switch AppHost" aria-label="Switch AppHost">
          <span className={`pill__dot ${active.state}`} />
          <span className="apphost-switcher__name">{active.name}</span>
          <span className="apphost-switcher__count">{apphosts.length}</span>
        </SelectTrigger>
        <SelectContent className="apphost-switcher__menu" position="popper" align="start">
          {apphosts.map((apphost) => (
            <SelectItem
              key={apphost.id}
              value={apphost.id}
              className={`apphost-switcher__item${apphost.active ? " apphost-switcher__item--active" : ""}`}
            >
              <span className="apphost-switcher__item-content">
                <span className={`pill__dot ${apphost.state}`} />
                <span className="apphost-switcher__item-name">{apphost.name}</span>
              </span>
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}
