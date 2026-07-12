import type { DeckConfig } from "../api/types";
import type { ThemeChoice } from "../lib/theme";
import type { TimeFormatChoice } from "../lib/timeFormat";
import { Button, Dialog } from "../toolkit";

const choices: Array<{ value: ThemeChoice; label: string }> = [
  { value: "system", label: "System" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
];

const timeChoices: Array<{ value: TimeFormatChoice; label: string }> = [
  { value: "system", label: "System" },
  { value: "12-hour", label: "12-hour" },
  { value: "24-hour", label: "24-hour" },
];

export function SettingsDialog({
  open,
  config,
  themeChoice,
  onThemeChoiceChange,
  timeFormatChoice,
  onTimeFormatChoiceChange,
  onClose,
}: {
  open: boolean;
  config: DeckConfig | null;
  themeChoice: ThemeChoice;
  onThemeChoiceChange: (choice: ThemeChoice) => void;
  timeFormatChoice: TimeFormatChoice;
  onTimeFormatChoiceChange: (choice: TimeFormatChoice) => void;
  onClose: () => void;
}) {
  return (
    <Dialog
      open={open}
      title="Settings"
      onClose={onClose}
      className="shell-dialog settings-dialog"
      actions={<Button onClick={onClose}>Close</Button>}
    >
      <fieldset className="settings-group">
        <legend>Theme</legend>
        <div className="settings-radio-group">
          {choices.map((choice) => (
            <label key={choice.value} className="deck-radio">
              <input
                type="radio"
                name="settings-theme"
                value={choice.value}
                checked={themeChoice === choice.value}
                onChange={() => onThemeChoiceChange(choice.value)}
              />
              {choice.label}
            </label>
          ))}
        </div>
      </fieldset>
      <fieldset className="settings-group">
        <legend>Time format</legend>
        <div className="settings-radio-group">
          {timeChoices.map((choice) => (
            <label key={choice.value} className="deck-radio">
              <input
                type="radio"
                name="settings-time-format"
                value={choice.value}
                checked={timeFormatChoice === choice.value}
                onChange={() => onTimeFormatChoiceChange(choice.value)}
              />
              {choice.label}
            </label>
          ))}
        </div>
      </fieldset>
      <dl className="settings-versions">
        <div><dt>Dashboard version</dt><dd>{config?.version || "Unknown"}</dd></div>
        <div><dt>Runtime version</dt><dd>{config?.runtimeVersion || "Not reported"}</dd></div>
      </dl>
    </Dialog>
  );
}
