export const toolkitFeatures = {
  "TK-BROWSER-001": "The toolkit loads without browser, page, or network errors.",
  "TK-SHELL-001": "The Fluent provider applies Deck typography, color, radius, and theme tokens.",
  "TK-ACTIONS-001": "Secondary, primary, danger, ghost, and icon buttons expose consistent behavior.",
  "TK-DIALOG-001": "Confirmation dialogs support cancel, confirm, and Escape dismissal.",
  "TK-DRAWER-001": "Drawers expose an accessible title, footer actions, close command, and Escape dismissal.",
  "TK-DATA-001": "Data tables retain semantic headers, filtering, row content, and empty results.",
  "TK-STATUS-001": "Badges and resource state indicators expose every semantic status tone.",
  "TK-EMPTY-001": "Empty states expose an icon, title, and supporting content.",
  "TK-NOTIFICATION-001": "Notifications expose semantic intent, actions, links, and explicit dismissal.",
  "TK-A11Y-001": "The playground has a reviewed accessibility-tree contract.",
  "TK-RESPONSIVE-001": "Toolkit controls remain contained and usable at desktop and mobile widths.",
} as const;

export type ToolkitFeatureId = keyof typeof toolkitFeatures;

export function getMissingToolkitFeatures(covered: ReadonlySet<ToolkitFeatureId>): ToolkitFeatureId[] {
  return (Object.keys(toolkitFeatures) as ToolkitFeatureId[]).filter((feature) => !covered.has(feature));
}
