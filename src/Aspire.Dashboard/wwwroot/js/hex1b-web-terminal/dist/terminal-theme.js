const light = `
  --cp-terminal-surface: #ffffff;
  --cp-terminal-text: #242424;
  --cp-terminal-text-muted: #5c5c5c;
  --cp-terminal-border-strong: #919191;
  --cp-terminal-accent: #b11f4b;
  --cp-terminal-accent-soft: rgba(177, 31, 75, 0.08);
  --cp-terminal-danger: #dc2626;
`;
const dark = `
  --cp-terminal-surface: #292929;
  --cp-terminal-text: #dedede;
  --cp-terminal-text-muted: #919191;
  --cp-terminal-border-strong: #5f5f5f;
  --cp-terminal-accent: #fd8ea1;
  --cp-terminal-accent-soft: rgba(253, 142, 161, 0.14);
  --cp-terminal-danger: #f87171;
`;
// Shared embedding tokens take precedence; defaults never overwrite inherited --cp-* colors.
export const terminalThemeCss = `
  :host {
    ${light}
    --cp-terminal-font-family: "Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif;
  }
  @media (prefers-color-scheme: dark) { :host { ${dark} } }
  :host-context([data-theme="light"]) { ${light} }
  :host-context([data-theme="dark"]) { ${dark} }
  .viewport {
    --cp-view-surface: var(--cp-surface, var(--cp-terminal-surface));
    --cp-view-text: var(--cp-text, var(--cp-terminal-text));
    --cp-view-text-muted: var(--cp-text-muted, var(--cp-terminal-text-muted));
    --cp-view-border-strong: var(--cp-border-strong, var(--cp-terminal-border-strong));
    --cp-view-accent: var(--cp-accent, var(--cp-terminal-accent));
    --cp-view-accent-soft: var(--cp-accent-soft, var(--cp-terminal-accent-soft));
    --cp-view-danger: var(--cp-danger, var(--cp-terminal-danger));
    --cp-view-font-family: var(--cp-font-family, var(--cp-terminal-font-family));
  }
`;
//# sourceMappingURL=terminal-theme.js.map