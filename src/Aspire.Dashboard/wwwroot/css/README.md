# Dashboard Stylesheets

Styles load after Fluent's reboot stylesheet in this order:

1. `design.css`: local fonts, semantic design tokens, light/dark theme values, and console palette.
2. `controls.css`: Fluent adaptations and shared button, field, menu, tab, grid, and scrollbar recipes.
3. `layout.css`: shared page structure, scrolling, toolbars, and dialog layout.
4. Highlighting and Markdown styles, followed by the generated isolated component stylesheet.

Use component-scoped styles for local composition and deliberate variants. Prefer existing semantic classes and custom properties to copying a control's dimensions or interaction states. Do not merge different semantic colors just because their current values match.

Grid spacing is opt-in with `aspire-spaced-grid`. `aspire-scroll-border-grid` preserves the bottom row border on scrolling log and trace lists without affecting empty/loading rows. `aspire-content-tabs` places shared tab-strip chrome on the tab list rather than the content wrapper. Ordinary tabs retain their wrapper chrome.

Content tabs use their containing layout's width and visible overflow; the surrounding pane owns scrolling. Reserve `fit-content` sizing and tab-strip scrolling for ordinary tabs so chart contents cannot determine their own available width.

Fluent-generated dialog actions also use the shared button geometry, since those buttons cannot always receive a class from the caller. Keep variant backgrounds, wrapping, and sizes distinct. The 28px standard control height is not a replacement for compact actions, 32px grid/header actions, or navigation controls.

CSS isolation and shadow DOM are separate boundaries. Moving `::deep` selectors changes specificity and the required scoped ancestor. Root dialog `::part` overrides and provider-rendered content may need global rules. Do not move a rule solely because its selector names a component.

Validate shared styling changes with a rebuilt dashboard and before/after screenshots in both themes and at desktop/mobile widths. Include hover, pressed, disabled, keyboard focus, and forced colors where control recipes change. Screenshots made with injected styles are useful probes, but do not replace validation of the generated isolated stylesheet and published asset list.

## Higher-Risk Regression Checks

- Shared dialog buttons and fields: test nested Settings/Manage dialogs, interaction forms, Notifications, long localized labels, keyboard focus, dismissal, and Windows Contrast Themes. Generated actions and shadow parts have different selector boundaries from ordinary controls.
- Content tabs and grid spacing: switch Metrics between Graph and Table, resize around the 768px breakpoint, and exercise filters, histograms, empty/loading states, sorting, live updates, and scrolling. Every nested grid that needs shared spacing must opt in.
- Shared scrolling and text styles: test console logs, text and GenAI visualizers, long lines, line numbers, wrapping, and virtualization. Verify Resources graph/filter popups with a connected AppHost.
- Stylesheet order and theme tokens: check light/dark switching and Firefox/Safari as well as Chromium. Keep component-specific variants and native forced-color behavior intact.