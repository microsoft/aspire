import type { DeckConfig } from "../api/types";
import { changeCulture } from "../api/deck";
import type { ThemeChoice } from "../lib/theme";
import type { TimeFormatChoice } from "../lib/timeFormat";
import { Button, Dialog, NamedIcon, RadioGroup, Select } from "../toolkit";

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
  onManageData,
  onClose,
}: {
  open: boolean;
  config: DeckConfig | null;
  themeChoice: ThemeChoice;
  onThemeChoiceChange: (choice: ThemeChoice) => void;
  timeFormatChoice: TimeFormatChoice;
  onTimeFormatChoiceChange: (choice: TimeFormatChoice) => void;
  onManageData: () => void;
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
        <RadioGroup ariaLabel="Theme" value={themeChoice} options={choices} onValueChange={onThemeChoiceChange} className="settings-radio-group" />
      </fieldset>
      <fieldset className="settings-group">
        <legend>Time format</legend>
        <RadioGroup ariaLabel="Time format" value={timeFormatChoice} options={timeChoices} onValueChange={onTimeFormatChoiceChange} className="settings-radio-group" />
      </fieldset>
      {config?.culture && config.cultures && config.cultures.length > 0 ? (
        <div className="settings-language">
          <Select
            label="Language"
            value={config.culture}
            options={config.cultures.map((culture) => ({ value: culture.name, label: culture.displayName }))}
            onValueChange={(culture) => {
              if (culture === config.culture) return;
              const redirectUrl = `${window.location.pathname}${window.location.search}`;
              void changeCulture(culture, redirectUrl);
            }}
          />
          <div className="settings-subtext">The page reloads when the language changes.</div>
        </div>
      ) : null}
      <div className="settings-actions">
        <Button onClick={onManageData}><NamedIcon name="Database" size={16} /> Manage data</Button>
      </div>
      <dl className="settings-versions">
        <div><dt>Dashboard version</dt><dd>{config?.version || "Unknown"}</dd></div>
        <div><dt>Runtime version</dt><dd>{config?.runtimeVersion || "Not reported"}</dd></div>
      </dl>
    </Dialog>
  );
}
