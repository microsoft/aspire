import type { DeckConfig } from "../api/types";
import type { ThemeChoice } from "../lib/theme";
import { Button, Dialog } from "../toolkit";

const choices: Array<{ value: ThemeChoice; label: string }> = [
  { value: "system", label: "System" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
];

export function SettingsDialog({
  open,
  config,
  themeChoice,
  onThemeChoiceChange,
  onClose,
}: {
  open: boolean;
  config: DeckConfig | null;
  themeChoice: ThemeChoice;
  onThemeChoiceChange: (choice: ThemeChoice) => void;
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
      <dl className="settings-versions">
        <div><dt>Dashboard version</dt><dd>{config?.version || "Unknown"}</dd></div>
        <div><dt>Runtime version</dt><dd>{config?.runtimeVersion || "Not reported"}</dd></div>
      </dl>
    </Dialog>
  );
}
