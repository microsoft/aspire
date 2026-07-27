import { useState } from "react";

export type TimeFormatChoice = "system" | "12-hour" | "24-hour";

const storageKey = "aspire-deck-time-format";
let currentChoice = readChoice();

function readChoice(): TimeFormatChoice {
  const value = window.localStorage.getItem(storageKey);
  return value === "12-hour" || value === "24-hour" ? value : "system";
}

export function useTimeFormat(): [TimeFormatChoice, (choice: TimeFormatChoice) => void] {
  const [choice, setChoice] = useState<TimeFormatChoice>(currentChoice);
  const update = (nextChoice: TimeFormatChoice): void => {
    currentChoice = nextChoice;
    window.localStorage.setItem(storageKey, nextChoice);
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
