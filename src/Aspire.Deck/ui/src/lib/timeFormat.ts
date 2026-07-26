import { useState } from "react";

export type TimeFormatChoice = "system" | "12-hour" | "24-hour";

const storageKey = "aspire-deck-time-format";

// Storage access can throw rather than return null: browsers raise SecurityError when storage is
// partitioned or blocked (Safari private browsing, blocked third-party storage). Because the choice
// is read during module initialization, an unguarded read would stop the whole app from booting
// over a non-essential preference, so both directions fail soft and fall back to the system format.
function readStoredChoice(): string | null {
  try {
    return globalThis.localStorage?.getItem(storageKey) ?? null;
  } catch {
    return null;
  }
}

function writeStoredChoice(choice: TimeFormatChoice): void {
  try {
    globalThis.localStorage?.setItem(storageKey, choice);
  } catch {
    // The preference simply does not persist across reloads when storage is unavailable.
  }
}

let currentChoice = readChoice();

function readChoice(): TimeFormatChoice {
  const value = readStoredChoice();
  return value === "12-hour" || value === "24-hour" ? value : "system";
}

export function useTimeFormat(): [TimeFormatChoice, (choice: TimeFormatChoice) => void] {
  const [choice, setChoice] = useState<TimeFormatChoice>(currentChoice);
  const update = (nextChoice: TimeFormatChoice): void => {
    currentChoice = nextChoice;
    writeStoredChoice(nextChoice);
    setChoice(nextChoice);
  };
  return [choice, update];
}

export function timeFormatOptions(): Pick<Intl.DateTimeFormatOptions, "hour12"> {
  return currentChoice === "system" ? {} : { hour12: currentChoice === "12-hour" };
}

export function getTimeFormatChoice(): TimeFormatChoice {
  return currentChoice;
}
