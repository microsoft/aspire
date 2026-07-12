import { Button, Dialog } from "../toolkit";

const shortcutGroups = [
  { title: "Pages", shortcuts: [["R", "Resources"], ["C", "Console"], ["S", "Structured Logs"], ["T", "Traces"], ["M", "Metrics"]] },
  { title: "Dashboard", shortcuts: [["?", "Help"], ["Shift + S", "Settings"]] },
] as const;

export function HelpDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <Dialog
      open={open}
      title="Help"
      onClose={onClose}
      className="shell-dialog"
      actions={<Button onClick={onClose}>Close</Button>}
    >
      <a href="https://aka.ms/aspire/dashboard" target="_blank" rel="noreferrer noopener">Aspire dashboard documentation</a>
      <h3 className="shell-dialog__heading">Keyboard shortcuts</h3>
      <div className="shortcut-grid">
        {shortcutGroups.map((group) => (
          <section key={group.title} className="shortcut-group" aria-labelledby={`shortcut-${group.title.toLowerCase()}`}>
            <h4 id={`shortcut-${group.title.toLowerCase()}`}>{group.title}</h4>
            <dl>
              {group.shortcuts.map(([keys, description]) => (
                <div key={keys}><dt>{description}</dt><dd><kbd>{keys}</kbd></dd></div>
              ))}
            </dl>
          </section>
        ))}
      </div>
    </Dialog>
  );
}
