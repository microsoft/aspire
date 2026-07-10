export const toolkitFeatures = {
  "TK-BROWSER-001": "The toolkit loads without browser, page, or network errors.",
  "TK-SHELL-001": "The Fluent provider applies Deck typography, color, radius, and theme tokens.",
  "TK-PAGE-001": "Pages compose an accessible header, heading, actions, toolbar, and independently scrolling body.",
  "TK-ACTIONS-001": "Secondary, primary, danger, ghost, and icon buttons expose consistent behavior.",
  "TK-MENU-001": "Command menus expose icons, descriptions, disabled and destructive items, keyboard selection, and focus restoration.",
  "TK-DIALOG-001": "Confirmation dialogs support cancel, confirm, and Escape dismissal.",
  "TK-DRAWER-001": "Drawers expose an accessible title, footer actions, close command, and Escape dismissal.",
  "TK-DATA-001": "Data tables retain semantic headers, filtering, row content, and empty results.",
  "TK-STATUS-001": "Badges and resource state indicators expose every semantic status tone.",
  "TK-EMPTY-001": "Empty states expose an icon, title, and supporting content.",
  "TK-NOTIFICATION-001": "Notifications expose semantic intent, actions, links, and explicit dismissal.",
  "TK-SELECT-001": "Select controls expose labels, placeholders, disabled options, and controlled values.",
  "TK-CHECKBOX-001": "Checkboxes support checked, unchecked, mixed, and disabled states.",
  "TK-SECRET-001": "Sensitive values are masked by default and can be explicitly revealed and hidden.",
  "TK-TABS-001": "Tabs support pointer and keyboard selection while preserving inactive panel state.",
  "TK-ACCORDION-001": "Accordion sections expose controlled expanded state, counts, and disclosure semantics.",
  "TK-DIVIDER-001": "Horizontal and vertical dividers expose semantic orientation.",
  "TK-HIGHLIGHT-001": "Search highlighting is case-insensitive and preserves the original text.",
  "TK-A11Y-001": "The playground has a reviewed accessibility-tree contract.",
  "TK-RESPONSIVE-001": "Toolkit controls remain contained and usable at desktop and mobile widths.",
} as const;

export type ToolkitFeatureId = keyof typeof toolkitFeatures;

export function getMissingToolkitFeatures(covered: ReadonlySet<ToolkitFeatureId>): ToolkitFeatureId[] {
  return (Object.keys(toolkitFeatures) as ToolkitFeatureId[]).filter((feature) => !covered.has(feature));
}
