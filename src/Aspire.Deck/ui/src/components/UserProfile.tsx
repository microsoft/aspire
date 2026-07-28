import type { DeckUser } from "../api/types";
import { signOut } from "../api/deck";
import { Button } from "../toolkit";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

function getInitials(name: string): string {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((part) => part[0]?.toLocaleUpperCase() ?? "")
    .join("") || "?";
}

export function UserProfile({ user }: { user: DeckUser }) {
  const initials = getInitials(user.name);

  return (
    <div className="user-profile">
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" className="user-profile__trigger" aria-label={`User profile for ${user.name}`}>
            {initials}
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent className="user-profile__menu" aria-label="User profile" align="end" sideOffset={8}>
          <DropdownMenuLabel className="user-profile__label">Logged in as</DropdownMenuLabel>
          <div className="user-profile__identity">
            <div className="user-profile__avatar" aria-hidden="true">{initials}</div>
            <div>
              <div className="user-profile__name">{user.name}</div>
              {user.username ? <div className="user-profile__username">{user.username}</div> : null}
            </div>
          </div>
          <div className="user-profile__signout">
            <DropdownMenuItem onSelect={() => void signOut()}>Sign out</DropdownMenuItem>
          </div>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  );
}
