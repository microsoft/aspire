# Dashboard Fluent UI v4 to v5 Migration

## Purpose and Status

This is a migration specification and handoff record for continuing the Aspire Dashboard Fluent UI Blazor v4 to v5 work in a new conversation. It summarizes the final decisions and evidence from the migration session, including approaches that were tried and then removed.

**Recorded: 2026-09-07.** This is a point-in-time record, not a claim that the entire migration or every browser scenario is complete. Recheck source, package versions, runtime endpoints, and PR feedback before acting on time-sensitive information.

| Item | State at handoff |
| --- | --- |
| Repository | `microsoft/aspire` |
| PR | [#19431](https://github.com/microsoft/aspire/pull/19431) |
| Branch | `jamesnk/fluentui-v5-dashboard` |
| Last committed and pushed change | `b96c8482b177fe36170975c469212b4d84fefd53` |
| Fluent components and icons packages | `5.0.0-rc.6.26241.1` |
| v4 package used for comparison | `4.14.4` |
| Dashboard target | .NET 8, using the repository's local SDK; do not confuse SDK version with target framework |
| Pending code change | Four selector replacements in [MobileNavMenuTests.cs](../../tests/Aspire.Dashboard.Tests/Integration/Playwright/MobileNavMenuTests.cs), not yet committed or pushed |
| Latest review status checked | 41 threads total, one unresolved thread about those mobile-navigation test selectors |

### Start Here in the Next Session

1. Read this document and [AGENTS.md](../../AGENTS.md), plus applicable dashboard/test instructions.
2. Check `git status --short --branch` and `git log -1`. Preserve the pending mobile-navigation selector fix and any later user edits.
3. Refresh the PR's review threads. Resolved status is not proof a finding was fixed; some threads were resolved as superseded or explicitly deferred.
4. The pending selector fix changes all four occurrences of `fluent-menu.mobile-nav-menu` to `fluent-menu-list.mobile-nav-menu`, including embedded JavaScript. Equivalent live-browser checks passed; the outerloop test class itself was not run.
5. Do not automatically commit, push, resolve threads, trigger workflows, or change package versions. Those were separate explicit user requests during this session.
6. Use the existing v4 reference and current v5 apps for comparison, but rediscover availability and ports. Do not stop or modify the v4 reference.

The user previously requested hibernation once there were no unresolved comments. That was a completed, one-time action, not an instruction for subsequent sessions.

## Goals and Boundaries

- Preserve the final v4 dashboard's visual language, density, theme hierarchy, and workflows while using v5 APIs and rendering.
- Prefer Fluent's supported parameters, semantic color tokens, and native browser behavior over dashboard workarounds.
- Match rendered appearance and behavior, not v4 DOM identity. Many v4 selectors are meaningless against v5's different hosts, slots, and shadow trees.
- Keep light and dark themes equally supported. Check small viewports and keyboard operation as part of changes to shared controls.
- Do not redesign the dashboard, change its palette opportunistically, or reintroduce the removed FAST token architecture.
- Keep changes local to their owning abstraction. A global styling fix must be checked on adjacent consumers, not only the screenshot that motivated it.
- Follow the repository restrictions on generated API files, package/configuration changes, localization, and tests. Do not add new repository Playwright tests merely to record an ad hoc comparison; the user asked to keep those exploratory checks in ignored artifacts. Maintaining existing browser tests and adding focused component regressions is appropriate.

### Source of Truth

The final-v4 source revision recorded for the visual comparison was `0f3b3dc00e454f7115ffde0561e152568929a3c1`. A separate local checkout at `C:/Development/Source/aspire-codereview` supplied the running v4 TestShop reference. Its last known URL was `https://testshop.dev.localhost:57203/`.

Use v4 computed styles and screenshots alongside current source to establish intended appearance. Some early session notes refer to different ports, missing variables that have since been restored, or selectors that have since changed. Treat those notes as historical evidence, not current requirements.

## Implementation Map

| Responsibility | Main implementation |
| --- | --- |
| Theme tokens, fonts, semantic surfaces, shared control recipes | [design.css](../../src/Aspire.Dashboard/wwwroot/css/design.css) |
| Shared layout, grids, menus, responsive dialogs | [layout.css](../../src/Aspire.Dashboard/wwwroot/css/layout.css) |
| Shell and navigation | [MainLayout.razor.cs](../../src/Aspire.Dashboard/Components/Layout/MainLayout.razor.cs), [MainLayout.razor.css](../../src/Aspire.Dashboard/Components/Layout/MainLayout.razor.css) |
| Mobile navigation | [MobileNavMenu.razor](../../src/Aspire.Dashboard/Components/Layout/MobileNavMenu.razor), [MobileNavMenu.razor.cs](../../src/Aspire.Dashboard/Components/Layout/MobileNavMenu.razor.cs) |
| Dialog abstraction and close completion | [DashboardDialogService.cs](../../src/Aspire.Dashboard/Model/DashboardDialogService.cs), [DashboardDialogParameters.cs](../../src/Aspire.Dashboard/Model/DashboardDialogParameters.cs) |
| Navigation-driven dialog dismissal | [DashboardDialogProvider.cs](../../src/Aspire.Dashboard/Components/Layout/DashboardDialogProvider.cs), [NavigationDialogService.cs](../../src/Aspire.Dashboard/Model/NavigationDialogService.cs) |
| Shared menus | [AspireMenu.razor](../../src/Aspire.Dashboard/Components/Controls/AspireMenu.razor), [AspireMenu.razor.cs](../../src/Aspire.Dashboard/Components/Controls/AspireMenu.razor.cs), [AspireMenuButton.razor](../../src/Aspire.Dashboard/Components/Controls/AspireMenuButton.razor) |
| Resource graph menu lifecycle | [app-resourcegraph.js](../../src/Aspire.Dashboard/wwwroot/js/app-resourcegraph.js), [Resources.razor.cs](../../src/Aspire.Dashboard/Components/Pages/Resources.razor.cs), [Resources.razor.css](../../src/Aspire.Dashboard/Components/Pages/Resources.razor.css) |
| Command progress/result toasts | [DashboardCommandExecutor.cs](../../src/Aspire.Dashboard/Model/DashboardCommandExecutor.cs) |
| Message bars | [DashboardMessageBar.razor](../../src/Aspire.Dashboard/Components/Controls/DashboardMessageBar.razor), [DashboardMessageBar.razor.css](../../src/Aspire.Dashboard/Components/Controls/DashboardMessageBar.razor.css) |
| Waiting for trace/span data | [TraceLinkHelpers.cs](../../src/Aspire.Dashboard/Model/TraceLinkHelpers.cs) |
| Grid value actions | [GridValue.razor](../../src/Aspire.Dashboard/Components/Controls/GridValue.razor), [ResourceActions.razor](../../src/Aspire.Dashboard/Components/Controls/ResourceActions.razor) |
| Metrics dimension filters | [ChartFilterPopover.razor](../../src/Aspire.Dashboard/Components/Controls/Chart/ChartFilterPopover.razor), [ChartFilterTags.razor](../../src/Aspire.Dashboard/Components/Controls/Chart/ChartFilterTags.razor) |
| Narrow browser/component workarounds | [app.js](../../src/Aspire.Dashboard/wwwroot/js/app.js) |

## Styling Decisions

### Theme Architecture

The shared theme lives in `design.css`, with light defaults under `:root` and dark overrides under `[data-theme="dark"]`. `layout.css` handles shared structure, and component-scoped styles handle local layout exceptions. Keep `app.css`, `tokens.css`, old FAST DesignToken JavaScript, and obsolete design-system plumbing removed.

The restored typography uses Geist for general UI, Poppins for display headings, and Cascadia Mono/Geist Mono for monospace content. Fonts are local assets. Preserve the existing design rather than substituting a new default stack.

Representative restored colors, useful for comparisons:

| Semantic value | Light | Dark |
| --- | --- | --- |
| Page / neutral background 2 | `#f7f7f7` | `#221e2d` |
| Control / neutral background 1 | `#ffffff` | `#2d2937` |
| Neutral background 3 | `#ebebeb` | `#312e3c` |
| Neutral foreground 1 | `#1a1a1a` | `#ffffff` |
| Brand background / primary icon | `#512bd4` | `#b9aaee` |
| Neutral stroke 1 | `#d2d2d2` | `#494553` |

The source contains more precise recipes for primary hover/pressed contrast, message bars, log severity, GenAI content, focus outlines, resource palettes, and terminal ANSI colors. Do not replace these with a single generic brand-color rule. The dark primary foreground intentionally uses a darker purple recipe, not the same foreground as the light theme.

### Host, Shadow Parts, and Scoped CSS

- v4 buttons generally painted on an internal native button exposed as `::part(control)`. v5 button backgrounds, borders, and colors primarily paint on the `fluent-button` host.
- Inspect the live shadow DOM before adding a `::part(...)` selector. A plausible part name does not mean the control exposes that part.
- Blazor CSS isolation and web-component shadow DOM are separate boundaries. Child component output, such as a `FluentIcon` SVG, does not automatically have its parent's scope attribute.
- Check generated scoped selectors as well as source CSS. For example, `.marker ::deep svg` and `::deep .marker > svg` have different scoped-ancestor requirements.
- Plain `wwwroot` CSS and JavaScript changes were usually visible after a fresh page load through development static assets. Scoped `.razor.css` changes require a build to regenerate the stylesheet. Browser-only injected styles are useful probes, not final validation of a rebuilt app.
- Forced-colors behavior matters. Normal-theme disabled color overrides and radio color recipes are scoped to `@media (forced-colors: none)` so system-color behavior is not replaced indiscriminately.

### Disabled Buttons

Final requirement: retain resting text/icon/background/border colors, fade the whole control to `opacity: 0.3`, use `cursor: not-allowed`, and suppress hover/pressed visual changes.

The initial fix added disabled exclusions to many hover selectors. The user rejected that repetition. The final design instead defines resting values and enforces them centrally:

```css
fluent-button:is([disabled], [disabled-focusable]) {
    opacity: 0.3 !important;
    cursor: not-allowed !important;
    transition: none !important;
}

@media (forced-colors: none) {
    fluent-button:is([disabled], [disabled-focusable]) {
        background: var(--aspire-button-background) !important;
        color: var(--aspire-button-foreground) !important;
        border-color: var(--aspire-button-border-color) !important;
    }
}
```

Read the actual rules for the additional icon tokens. Important details:

- Appearances and custom button classes supply `--aspire-button-background`, `--aspire-button-foreground`, and `--aspire-button-border-color`. Low-specificity `:where(...)` appearance selectors allow component-specific resting recipes to win.
- Defining a custom property does not paint an enabled button. Custom styles still apply it with `background`, `background-color`, `color`, or `border-color`. The central override only applies when disabled.
- Use the full `background` shorthand for disabled enforcement so neutral-button gradient layers are preserved, not flattened to one color.
- Loading buttons use `disabled-focusable` as well as disabled behavior. Do not ignore that state.
- Do not use `:state(disabled)` for buttons based on assumptions from other Fluent controls. An actual disabled button had `[disabled]` but did not match `:state(disabled)`.
- Explicitly colored icons bypass inherited disabled text colors. Host opacity fades those icons too. Do not multiply fades on multiple ancestors.
- Fluent Subtle icons have their own hover/pressed color rules. The disabled recipe pins those tokens to `currentColor`.
- The navigation cog/button icon hover recipe also has a resting-icon override; preserve it when changing shared disabled rules.
- `transition: none` cancels color/background/border interpolation; it does not itself prevent hover styling and does not stop CSS loading-spinner animations.
- Do not use `pointer-events: none` to fake this state. It breaks the not-allowed cursor and can cause click-through.

There are Subtle buttons even though application markup does not explicitly request `ButtonAppearance.Subtle`: Fluent's data-grid column headers render that appearance. Their resting backgrounds can be overridden by the grid; preserve those values when disabled.

### Grid Action Size and Hover

Icon-only buttons inside `.fluent-data-grid` have fixed width, min/max width, height, and min/max height of 32px, zero padding, and `flex-shrink: 0`.

`IconOnly="true"` was added to text-visualizer, mask/unmask, exception details, GenAI details in logs/traces/span grids, highlighted resource commands, and console-log shortcut buttons. Overflow menu buttons already derive icon-only status. Text buttons, details toolbars, and grid-header sizing were not globally changed to 32px. Resource action slots remain 32px and center-aligned.

Transparent hover backgrounds deliberately differ by grid behavior:

- Highlighting resource/log rows: use `--colorNeutralBackground2`, matching v4 action hover.
- Non-highlighting rows: `.fluent-data-grid tr:not([hover])` uses `--colorNeutralBackground3` so dimension filter hover is visible.

A broad override for every grid button previously regressed Resources and Structured Logs. Do not reintroduce it.

### Metrics Filter State

The dimension filter button uses `Transparent` when `AreAllValuesSelected is true`, and `Primary` for partial selection (`null`) or no selection (`false`). The icon uses brand color when unfiltered and `currentColor` when highlighted so primary contrast remains correct. Selection notifications rerender the component, and the popover anchor ID remains stable.

The user explicitly rejected using `Subtle` instead of `Transparent` for the unfiltered button. The shared non-highlighting-row hover recipe solves that visibility issue.

The `(All)` checkbox explicitly maps the non-null `Value` to `AreAllValuesSelected is true` while retaining nullable `CheckState` for partial selection. The selection regression exercises all -> partial -> all -> none -> all, not just the initial render.

## Navigation, Menus, and Graph Cogs

### Mobile Navigation

Mobile navigation is an inline `FluentMenuList`, not a popup `FluentMenu`. Using the latter caused the first Resources entry to disappear into popup behavior. Parameters live in the code-behind. Dividers are only between items, with no trailing divider.

Preserve the 40px rows, full-width layout, selected accent bar, theme-aware icons, and menu z-index that prevents notification dismiss buttons from overlaying the menu. The navigation launch icon was changed by the user to Size20; do not restore the earlier Size24 experiment.

The correct browser selector is `fluent-menu-list.mobile-nav-menu`. The pending change updates two locators and two embedded JavaScript queries in the existing test file. At 640x384, the checked behavior includes Tab entry, arrow navigation through Resources to Settings, focus-loss dismissal without stealing focus, Escape returning focus to `dashboard-navigation-button`, and 4px padding/scroll-padding keeping focused items visible.

### Shared Fluent Menus

- `AspireMenu` wraps `FluentMenu` and `FluentMenuList`. Nested `MenuItems` content contains items directly; do not create an extra nested list that Fluent itself will already supply.
- The indicator and start slots are separate in v5. Checkbox/radio indicators use `slot="indicator"`; leading icons use `slot="start"`; secondary actions use the end slot.
- Menu headers identify the targeted resource and are non-interactive, not extra selectable menu items.
- Native menu toggle notifications must drive open state, including Escape and light dismissal. Do not treat the completion of an open request as closure.
- Secondary actions, such as pinning a run, must not also invoke the row's primary action. The narrow registered-element workaround in `app.js` checks the composed event path for `.aspire-menu-secondary-action` and intercepts Fluent's click/keydown activation handlers.
- When a menu action opens another view/dialog, restore trigger focus before invoking the action, not afterward. Restoring afterward steals focus from the new surface.

### Submenu Gap Investigation

The dashboard's removed rule was:

```css
.aspire-menu-container fluent-menu-list[slot="submenu"] {
    margin-inline: 8px;
}
```

Controlled pointer tests on the actual resource menu showed that crossing this gap closed the submenu. Removing only the margin allowed horizontal entry. Diagonal movement toward upper items in a tall, upward-opening submenu can still dismiss it; that is a separate pointer-tolerance limitation, not proof the removed margin was a Fluent defect.

An upstream issue was requested initially, but investigation found the direct regression was Aspire styling. **No new upstream issue was created.** [Fluent UI issue #5207](https://github.com/microsoft/fluentui-blazor/issues/5207) concerned different submenu positioning behavior and was already closed.

### Resource Graph Cog: Final Decision

**Keep the original SVG cog and open the menu at the mouse cursor. Do not anchor this menu to an HTML overlay.**

The final small fix keeps the cog visible for the menu lifetime:

- `openResourceContextMenu` remembers the SVG trigger and sets its `aria-expanded` state while opening.
- `Resources.ContextMenuOpenChangedAsync` forwards menu state to `updateResourcesGraphContextMenu` in JavaScript.
- On actual closure, the graph clears the trigger's expanded state and updates highlights. On an open failure, it clears the state and rethrows.
- CSS includes `.resource-menu-cog[aria-expanded="true"]` in the visible/pointer-enabled states.
- The original right-click positioning, keyboard activation, SVG layout, and graph drag behavior remain in place.

Historical experiment, explicitly reverted at the user's request: anchoring required menu recreation for changed triggers; the existing SVG cog did not work as a CSS anchor in the tested Fluent/Edge setup. An HTML button in `foreignObject` still failed placement. Moving it outside the SVG into a synchronized HTML overlay worked, but added graph-position/zoom synchronization and was rejected. None of that anchoring, overlay, `foreignObject`, or same-cog toggle refactor should be resurrected just to preserve cog visibility.

## Dialogs and Interaction Lifecycles

### Responsive Dialogs and Native Modality

Dialogs and drawers fill the viewport at widths up to 768px through shared CSS. Their desktop size returns on resize without recreating the dialog. Preserve the scrollable-body and fixed-format layout behavior.

Representative desktop widths used during restoration: Settings 300px, Notifications 350px, filters 450px, Text/GenAI visualizers up to `min(1000px, 75vw)`, and interaction inputs up to `min(650px, 75vw)`. Inspect each current caller before applying a new size; these are not universal defaults.

Settings and Notifications use drawers on desktop as well as mobile. `DashboardDialogProvider` is a C# component with authorization, `BuildRenderTree`, and a navigation subscription that is disposed correctly. `NavigationDialogService` tracks instances to dismiss them during navigation.

Fluent v5's `DialogOptions.Modal` mapping is not the same for dialogs and drawers:

- For a dialog, the inspected version maps false to native `type="modal"` and true to `type="alert"`. Both call native `showModal()`; the distinction includes light dismissal.
- For a drawer, false means non-modal and true means modal.
- `DashboardDialogService` translates `PreventDismissOnOverlayClick` for dialogs while preserving the conventional modal flag for drawers. Do not simplify this based on the property name alone.

### Close Completion Ordering

`DashboardDialogReference.CloseAsync()` and its result overload now request Fluent closure and then await the wrapper's `Result`. That result completes only after `DashboardDialogService.CompleteAsync` runs `OnDialogClosing` and `OnDialogResult`.

This prevents a replacement dialog from opening while an older dialog's delayed callback can still clear `_openPageDialog` or restore focus. Regression tests gate both callbacks with `TaskCompletionSource` and verify both overloads stay incomplete until the gates are released. Do not weaken this to a sleep-based test or return only the Fluent close task.

`AspirePageContentLayout` also clears its toolbar-panel reference before awaiting closure to make repeated close requests safe. Preserve that ordering and the restored `OnParametersSetAsync` lifecycle.

### Notification Center Focus Investigation

The former notification-center stack-overflow workaround is not required for the tested v5 path. Keeping the notification drawer open while View response opens a visualizer is intentional.

The investigation tested Edge `152.0.4191.62` and Chrome `152.0.7977.82`, at 1440px and 390px widths, for ten viewport/browser/dismissal combinations. Evidence:

- Both drawer and response use native modal dialogs. The browser makes the lower drawer inert.
- Forward/reverse Tab did not enter the drawer; programmatic attempts to focus it were blocked.
- Background View response buttons were excluded from the accessibility tree.
- Escape, the Close button, and desktop backdrop clicks closed the response only.
- Focus returned to the original View response button and the drawer remained usable.
- No stack overflow or other page errors occurred.

Two misleading observations were resolved during testing: `document.activeElement === body` with `document.hasFocus() === false` meant focus had gone to browser chrome, not the background page; and Fluent's `FocusOnPreviousActiveElement` restores focus after a 25ms timeout, so immediate post-removal focus can be transient. Wait for the intended focus state, not an arbitrary delay. Tooltip/popover focus also affected early Escape probes; sending Escape from the text area produced reliable results.

Firefox/Safari and physical mobile devices were not covered. The review thread was resolved as superseded, not by adding another close-before-open workaround. Settings also now opens Manage Data over its drawer.

### Custom Footer and Trace Waiting

In the installed Fluent `FluentDialogBody`, `ActionTemplate` and default footer actions are mutually exclusive render branches. The old duplicate-Cancel review comment's assumption that both render simultaneously was not valid for this version. `UseCustomFooter` still records intent and controls the dashboard's footer options, but does not itself select the template branch.

The user subsequently chose to replace the trace-waiting message box with `InteractionsProgressDialog`:

- Content is `InteractionsProgressDialogViewModel { Message = unavailableText }`.
- The localized Cancel label is the primary action; `UseCustomFooter = true`.
- The component supplies the spinner and one Cancel action.
- Its Cancel button returns `DialogResult.Ok("cancel")`, so the waiting helper must treat both `result.Cancelled` and `result.Value is "cancel"` as cancellation.
- When data arrives, close with successful boolean result and break the polling loop immediately. A test exposed a second-close race when the loop relied only on later token cancellation.
- Already-available data does not show a dialog. Tests cover that case, later arrival, dismissal, and progress cancellation.

Do not change the shared progress dialog's result contract merely to suit this caller; the interaction provider has its own progress protocol. Broader external-cancellation behavior was not redesigned as part of this change.

### Required-Field Accessibility

`InteractionInputField` previously used an asterisk with both `aria-label="required"` and `aria-hidden="true"`, making the accessible label ineffective. It now renders an exposed `role="img"` indicator with a field-derived ID and the localized `Dialogs.FieldRequired` text, "A value is required."

The file Browse button references the required indicator and any field description through `aria-describedby`, since a button does not have native input-required semantics. Text inputs retain their own required-state support. The component uses a `DialogsResources` alias: a `Dialogs` type/alias collides with nested component namespaces in this area.

Regression coverage includes required/optional text and file inputs. The browser accessibility tree confirmed the Receipt file button's accessible name and required description. No new resource strings or hand-edited translations were necessary.

## Toasts, Message Bars, and Manage Data

### Toast State and Colors

The dashboard retains a `ToastOptions` instance for progress -> success/error transitions. Notification updates cause the toast provider to rerender without replacing the component. The dashboard manages the result lifetime because mutating options does not restart Fluent's rendered lifetime automatically; a dismissed toast may need a new ID while the previous one is exiting.

Custom icon overrides bypass Fluent's default colored intent icons. `DashboardCommandExecutor.GetIntentIcon` now calls `WithColor(Color.Success/Warning/Error/Info)` on the existing Size24 icons. The progress Flash icon remains unchanged. A success toast was verified with `fill: var(--success)`, computed green in both themes.

Use Stress's no-op Icon test highlighted command to check success toasts. HTTP commands such as Increment counter may be disabled while service parameters remain unresolved; absence of a toast after clicking a disabled command is not evidence of a color bug.

### Final Message-Bar Markup

Final content flows inline and wraps together:

- Conditional title span `.dashboard-message-bar-title`, semibold with 8px trailing logical padding.
- Message span `.dashboard-message-bar-message`, using normal inline span layout and `Content.Message.TrimEnd()` on both plain-text and markup branches.
- Conditional link wrapper `.dashboard-message-bar-link`, with 8px leading logical padding.

The final padding is 8px, a user adjustment after an earlier 5px request. Preserve it unless asked otherwise.

Rejected/superseded iterations: flex/gap content layout, `display: inline-flex` on the message, explicit `@(" ")` separators, and unconditional wrapper spans intended to preserve Razor whitespace. The unconditional-span approach did preserve generated whitespace, but the user replaced it with classed conditional spans and padding. The explanatory Razor comment for that old approach was removed too.

`TrimEnd()` removes trailing whitespace from the source string; it is not an HTML parser and does not remove whitespace inside nested closing tags or encoded nonbreaking spaces. Do not claim more than it does. Single-paragraph interaction Markdown suppresses surrounding paragraph markup through the existing Markdown helper.

The user explicitly removed the new `MessageBarContent_SeparatesTitleMessageAndLink` test. Do not reintroduce that test just because it appears in old transcript excerpts.

### File Picker and Loading Actions

Manage Data's import/export/remove buttons use `IconStart` with colored icons so Fluent can replace them with its loading spinner. Manually slotted icons do not get the same automatic replacement behavior.

`app.js` intercepts bubbling native file-input `cancel` events only for file inputs inside dialogs/drawers. Canceling the picker or selecting the same file must not cancel the containing dialog. Escape and native dialog cancellation must still reach Fluent. Do not broaden this into suppressing every cancel event.

## Other Compatibility Work to Preserve

- The migration returned the dashboard and relevant tests to net8 after temporary net9 targeting/workarounds. [Aspire.Dashboard.csproj](../../src/Aspire.Dashboard/Aspire.Dashboard.csproj) retains `RollForward=Major` and the Roslyn Razor tokenizer feature. Do not reintroduce temporary hosting or framework overrides to solve unrelated build errors.
- The login token input was updated to the v5 password-input parameter. A generic text-input default would expose tokens as ordinary text.
- The Fluent module script must not be made `async`; component registration ordering matters during Blazor startup.
- [App.razor](../../src/Aspire.Dashboard/Components/App.razor) loads Fluent reboot styles, then shared dashboard styles, then generated scoped CSS. `app.js` runs before the Fluent module so its narrow element-registration customizations can take effect. Do not reorder these casually.
- [BlazorScript.razor](../../src/Aspire.Dashboard/Components/BlazorScript.razor) selects versioned Blazor JavaScript for newer runtimes despite the net8 target. [BlazorAssets.targets](../../src/Aspire.Dashboard/BlazorAssets.targets) obtains .NET 10/11 assets from `Microsoft.AspNetCore.App.Internal.Assets` and patches the `isComposing` event-payload field that older compatible runtimes reject. Preserve this compatibility work rather than changing target frameworks to hide it.
- The asset target also received a copy/patch concurrency review. The current source has a named mutex around the patch task, with `Copy` outside it. Do not infer that the whole operation is serialized merely because that review thread was resolved. Recheck this boundary if working on parallel RID builds; no new concurrency fix is proposed by this handoff.
- Dropdown styling has a narrow adopted-stylesheet workaround because the relevant control is not exposed as a public CSS part in the inspected build. It supplies border/background/focus styling and removes conflicting pseudo-elements. This is an existing exception, not a pattern to spread to all controls.
- The proposed `--aspire-dropdown-min-width` workaround was explicitly removed. Native 160px minimum behavior was accepted.
- Scrollbars reset `scrollbar-color` under Chromium pseudo-element support, because inherited standard scrollbar colors can prevent the intended `::-webkit-scrollbar` recipe from taking effect. Preserve the separate Firefox fallback.
- Radio appearance uses native v5 state selectors with normal-theme overrides. Do not assume button and radio disabled/checked state contracts are identical.
- Text-visualizer-specific mobile wrapping was removed from global CSS by the user; do not blindly restore it from older notes.
- FluentOverflow's `MaxRenderedItems` is not a DOM-rendering cap. Both URL overflow and metric tags currently render their full source collections and retain TODOs. The first item is fixed; indices in the managed overflow list therefore start at the second source item. Existing popover/selection calculations account for that with an index offset. Preserve the offset and do not infer bounded DOM solely from `MaxRenderedItems`.

## Validation Playbook

### Scope and Readiness

For a change, identify the owning code path and one cheap check that could disprove the hypothesis. Validate that slice immediately after editing, then broaden only to adjacent consumers at risk.

Use a matrix appropriate to the change:

| Surface | Important states |
| --- | --- |
| Buttons | Rest, hover, pressed, disabled, disabled-focusable, loading, re-enabled, explicitly colored and inherited icons |
| Grids | Header vs body, highlighting vs non-highlighting rows, selected rows, masked/unmasked, virtualized/offscreen controls |
| Menus | First open, reopen, Escape, outside click, focus loss, secondary action, nested submenu pointer paths, current trigger state |
| Dialogs | Open/close callbacks, replacement ordering, native modal stack, Escape vs button/backdrop dismissal, returned focus, resize while open |
| Mobile navigation | 640x384 high-zoom-like layout, Tab, arrows to last item, focus padding, Escape, focus leaving menu |
| Themes/layout | Light and dark; typically 1440x1000 or 1280x900, 390x844, and targeted breakpoint checks |

Wait for actual application data and state, fonts, custom-element upgrades, and theme application. An initially empty grid is not proof that data is missing. A role locator or host visibility check can fail even when content exists in a shadow dialog or collapsed section.

### Build and Unit/Component Tests

Run [restore.cmd](../../restore.cmd) first in a fresh environment to install/select the repository SDK. Do not modify SDK or package configuration as part of routine validation.

```powershell
./restore.cmd
dotnet build src/Aspire.Dashboard/Aspire.Dashboard.csproj --no-restore -p:SkipNativeBuild=true -v:minimal
dotnet test --project tests/Aspire.Dashboard.Components.Tests/Aspire.Dashboard.Components.Tests.csproj --no-launch-profile -- --filter-class '*.DashboardDialogServiceTests' --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'
dotnet test --project tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj --no-launch-profile -- --filter-class '*.DashboardCommandExecutorTests' --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'
```

- This repository uses xUnit v3 and Microsoft.Testing.Platform. Never use VSTest `--filter`; put MTP filters after `--`.
- Exclude quarantined and outerloop tests in normal automated runs. Existing dashboard Playwright tests are outerloop. Equivalent checks against a running app are useful but must not be reported as running the actual test class.
- Use `--no-build` only after a matching build in the same session with no intervening source edits. If needed, add `--hangdump --hangdump-timeout 1m` after `--` for a bounded diagnostic run.
- Reuse [FluentUISetupHelpers.cs](../../tests/Aspire.Dashboard.Components.Tests/Shared/FluentUISetupHelpers.cs), [TestDialogService.cs](../../tests/Shared/TestDialogService.cs), and existing test files. A new `SetupVoid` can wait forever unless explicitly completed with `SetVoidResult()` when the production path awaits it.
- Capture a component instance before disposing/recreating it; inspecting a disposed bUnit wrapper throws.
- `TestStringLocalizer` can prefix resource keys with `Localized:`. Compare to its returned value rather than assuming it returns the bare key.
- The repo enforces source headers for new C# files; prefer suitable existing test files for small regressions and follow current repository instructions on new files.
- Clear any shell-level `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` override before authentication-related tests and restore it afterward. Child-process-only launch configuration avoids this leakage.
- Razor language-service errors have sometimes been stale despite a passing build. Do not add arbitrary imports to address false diagnostics; use the real compiler to decide.

Representative successful checks during this session, not a single full-suite gate:

- 14 chart-filter component tests plus live selection/hover checks.
- 21 disabled-button configurations in both themes, including live navigation and Manage Data styles, with enabled-state recovery.
- Four command-executor tests and live success toast color checks.
- 30 resource-details/structured-log/trace tests and live grid size checks across themes/viewports.
- 19 focused dialog/accessibility tests plus 53 adjacent layout/provider/interaction tests; live accessibility-tree verification of required file input description.
- Ten dialog-service tests after the progress-dialog change; live spinner/Cancel verification.
- The final cursor-menu visibility regression and live pointer/dismissal checks. The earlier 50-test menu/resource run validated a subsequently reverted anchoring iteration, so do not cite it alone as validation of final code.

### Local Playwright Harness

The session used the Node Playwright package already bundled with the .NET test output. This is not `@playwright/test` and does not export its `expect`; use Node assertions or the appropriate .NET test API.

From an ignored script in `artifacts/dialog-comparison/`:

```javascript
const { chromium } = require('../bin/Aspire.Dashboard.Tests/Debug/net8.0/.playwright/package');
const assert = require('node:assert/strict');

const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
    const context = await browser.newContext({
        ignoreHTTPSErrors: true,
        viewport: { width: 1440, height: 1000 },
        colorScheme: 'light'
    });
    const page = await context.newPage();
    // Navigate to a discovered local endpoint and wait for the actual target state.
} finally {
    await browser.close();
}
```

This excerpt belongs inside an async function when used in a `.cjs` script. Prefer readable artifact scripts over long `node -e` commands: PowerShell quoting, truncation, and mismatched braces caused repeated wasted runs in this session.

- Use separate fresh contexts for themes and v4/v5. Existing `currentTheme` cookies can override system color preference; verify `html[data-theme]`. When explicitly setting the cookie, values are `Light`/`Dark`, while the DOM attribute is lowercase.
- Wait for animations before exact computed-style assertions. A useful check is `element.getAnimations({ subtree: true }).every(animation => animation.playState !== 'running')`. Reduced motion alone did not eliminate every interpolated `oklab(...)` value. Capture screenshots with `animations: 'disabled'`.
- v4 measurements often require the internal `[part='control']`; v5 button measurements usually use the host. Compare effective opacity, including relevant ancestors, not only icon fill strings.
- Use `fluent-dialog dialog[open]` or `fluent-drawer dialog[open]` when the custom-element host has no layout box. Playwright pierces open shadow roots for CSS queries.
- Inspect deep active elements and their owning dialog. Combine focus state with `document.hasFocus()`. Use CDP `Accessibility.getFullAXTree` for computed names/descriptions and background-inert checks.
- When adapting .NET `EvaluateAsync` scripts to Node, explicitly invoking a stringified function as `(<function source>)()` avoids differences caused by raw multiline string evaluation.
- For nested menus, broad text matches also match parents containing the submenu. Prefer exact title/role matches. Step the mouse along an actual path to test hover closure; a locator click can conceal or provoke movement issues.
- Accordion heading text can be covered by the shadow button. Target `[part=button]`, check whether the section is already expanded, and restrict measurements to visible controls.
- SVG icons may cover a node's circle. For genuine pointer tests use known on-screen coordinates; synthetic `dispatchEvent` is useful for lifecycle isolation but is not evidence that normal hit-testing works.
- Do not print environment-variable values, login tokens, file contents, or credential-like test results unnecessarily. Close temporary browser contexts and cancel synthetic pending interactions.

### Telemetry for Reproducible Browser Checks

Use a small, distinct synthetic resource rather than depending on workload timing. Discover the dashboard resource's `otlp-http` link after every restart; ports vary independently of the fixed frontend URL. Chromium resolves `*.dev.localhost`; Node HTTP requests in this environment were more reliable with the same endpoint port and hostname `localhost`.

Useful scenarios:

- A two-value gauge with dimension `http.method = GET/POST` exercises dimension-filter all/partial/none/reset states.
- A single span/log with `gen_ai.operation.name = chat` and `gen_ai.provider.name = openai` exercises GenAI details buttons. Use valid trace/span IDs and nanosecond timestamps represented as strings.
- A GenAI log referencing an intentionally absent span opens the trace-waiting progress dialog. Canceling must not launch a visualizer. Use a fresh trace ID so earlier samples do not satisfy the lookup.
- Stress's `icon-commands` group has a no-op success action. `result-commands` has Validate Config returning synthetic errors with response data. `interaction-commands` exposes notification/input/progress samples. Read each command first; names such as database migration or connection-string generation are not sufficient proof a command is harmless.

Resources/details often need to be opened through the actual Actions -> View details flow; a guessed `?resource=` URL did not reliably expose the expected property controls. Resource service startup may be blocked by unresolved parameters or an unhealthy container runtime. Do not silently populate secrets or change infrastructure to make a UI test run.

## Runtime Operations

Machine-local session context, not portable deployment configuration:

| App | Last known frontend | Project |
| --- | --- | --- |
| Current Stress | `https://Stress.dev.localhost:16309/` | [Stress.AppHost.csproj](../../playground/Stress/Stress.AppHost/Stress.AppHost.csproj) |
| Current TestShop | `https://TestShop.dev.localhost:16319/` | [TestShop.AppHost.csproj](../../playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj) |
| v4 reference TestShop | `https://testshop.dev.localhost:57203/` | Separate `aspire-codereview` checkout; do not modify or stop |

The user's final launch preference was **anonymous access, without isolation**. Earlier isolated runs produced many now-obsolete ports in scripts. The current apps were left running for manual testing; verify availability instead of trusting old PIDs.

Use `aspire start --apphost <explicit project> --non-interactive` for background launch. Set anonymous access only in the launched process environment where possible. For example, from the repository root:

```powershell
$previousAnonymous = $env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS
try {
    $env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS = 'true'
    aspire start --apphost playground/Stress/Stress.AppHost/Stress.AppHost.csproj --non-interactive
} finally {
    if ($null -eq $previousAnonymous) {
        Remove-Item Env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS -ErrorAction SilentlyContinue
    } else {
        $env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS = $previousAnonymous
    }
}
```

Stress and TestShop share dashboard outputs in this checkout. Before rebuilding locked outputs, stop only those explicit AppHosts with `aspire stop --apphost <project> --non-interactive`. After a successful build, restart them with `--no-build` to avoid a second app rebuilding binaries already in use. Never kill every process named `Aspire.Dashboard` or `dotnet`; that can destroy the v4 reference and unrelated sessions.

A plain CSS change usually does not require an AppHost restart. If a restart is necessary, restore the prior anonymous/non-isolated setup and report the URL. Do not assume all resources are healthy just because the dashboard starts.

## Local Artifacts and Tools

All following paths are ignored, machine-local artifacts. They may disappear in another checkout and often hard-code old ports or transient scope IDs. Inspect before reuse; do not commit them as production tests.

| Artifact under `artifacts/dialog-comparison/` | Purpose |
| --- | --- |
| `validate-dimension-highlight.cjs` | Seeds a small metric; checks all/partial/none/reset, icon contrast, hover, and v4 comparison |
| `validate-grid-button-states.cjs` | Resource/log grid hover and disabled-state comparison against v4 |
| `validate-disabled-buttons.cjs` | Rest/hover/pressed, disabled and disabled-focusable, custom classes, and enabled-state recovery |
| `validate-genai-grid-sizing.cjs` | Seeds GenAI telemetry and checks 32x32 actions in logs, traces, and span details |
| `investigate-notification-focus.cjs` | Edge/Chrome modal isolation and focus-restoration investigation |
| `notification-focus-results.json` | Recorded focus evidence, including delayed restoration |
| `validate-responsive-dialogs.cjs`, `validate-mobile-navigation.cjs`, `validate-import-cancel.cjs` | Earlier dialog/mobile/import investigations; update stale endpoint assumptions |
| `dimension-highlight/`, `grid-button-states/`, `disabled-buttons/`, assorted PNGs | Screenshots and computed-state evidence |

Additional earlier output exists under `artifacts/visual-comparison/`, `artifacts/query-optimization/`, and other ignored directories. An old `validate-after.cjs` has obsolete dialog/body/footer expectations and should not be treated as authoritative.

`ilspycmd` was useful for inspecting the installed Fluent assembly's actual render and close behavior. `dotnet-inspect` is also installed, but this machine's CLI did not accept the skill's `--oneline` examples; `member --help`, `--library`, `-m`, and `-n` worked. Prefer the already-restored package over acquiring another version to answer a question about current behavior. `rg` was unavailable in this session; repository search tools were used instead.

Full transcript and session-memory files were useful historical sources, but this document is intended to make them unnecessary for normal continuation. Never copy credentials or enormous raw terminal histories into a spec or PR comment.

## Review Disposition and Remaining Work

At the last refresh, the only unresolved thread was [the mobile-navigation selector comment](https://github.com/microsoft/aspire/pull/19431#discussion_r3944732158). Its four-line fix is in the worktree, not the pushed PR. Equivalent browser checks passed, but do not mark the thread fixed on GitHub before the user authorizes publishing the fix.

Earlier threads were handled as follows:

| Finding | Final disposition |
| --- | --- |
| Dialog close returns before wrapper callbacks | Fixed and pushed in `154570bb8b`; deterministic delayed-callback coverage; thread resolved |
| Required indicators hidden from accessibility | Fixed and pushed in `154570bb8b`; file-button description verified in browser; thread resolved |
| Trace waiting duplicates Cancel | Original premise did not match inspected v5 renderer; subsequently replaced with progress dialog in `0f0d7acdeb`; thread resolved |
| Notification-center focus traps | Investigated across Edge/Chrome and responsive sizes; native modal isolation works; resolved as superseded without restoring old workaround |
| Metric tag DOM cap | Explicitly accepted/deferred by the user; TODO retained; resolved as deferred, not fixed |
| Graph menu resets expanded state immediately | Fixed in `b96c8482b1`; original cursor menu retained, cog visible until dismissal; thread resolved |

Refresh feedback rather than assuming the table is still exhaustive. All 40 threads were resolved at one point; a later review added the 41st.

### Residual Risks, Not New Fix Requests

- Metric tags still render the full dimension collection. `MaxRenderedItems = 20` limits Fluent's overflow payload, not all child DOM. A high-cardinality render cap remains deliberately deferred.
- URL overflow likewise retains a pre-render-cap TODO in [UrlsColumnDisplay.razor](../../src/Aspire.Dashboard/Components/ResourcesGridColumns/UrlsColumnDisplay.razor). Historical notes about a 20-item cap must not be read as proof that such a cap remains implemented.
- Early analysis raised two navigation-cancellation possibilities: closing a filter can invoke deferred navigation and bounce back to filtered logs; navigation before an Opening callback can miss a pending dialog. These were not revalidated or fixed as part of the final thread-resolution pass. Treat them as investigation leads, not confirmed current defects or permission to refactor broadly.
- Diagonal navigation into a tall submenu remains less tolerant than horizontal entry. No upstream issue was filed from the gap investigation.
- Shadow-DOM workarounds depend on the installed Fluent implementation and need reevaluation on package upgrades. Their existence is not a reason to add more monkeypatches without evidence.
- Current browser checks are strong evidence for the exercised paths, not proof of Safari/Firefox, physical mobile, forced-colors, every interaction input, or whole-app accessibility compliance.
- Existing outerloop browser tests can retain stale v4 selectors or lifecycle expectations despite passing component tests. Update known affected tests locally; do not weaken their behavioral assertions to fit the migration.
- No claim is made that all repository tests, packaging gates, or all PR CI jobs were run in this conversation. Do not automatically trigger workflows.

## Commit Landmarks

These commits make it possible to inspect the final implementation without replaying every experiment:

| Commit | Main milestone |
| --- | --- |
| `20c49c1a70` | Initial Fluent UI Blazor v5 migration |
| `e724e0661b` | Resource graph menu interactions |
| `f0bcf7bbd5` | Broad v4 visual restoration |
| `4537299784` | Text Visualizer styling and dropdown text alignment |
| `6499a2a9a1` | RC6 update, net8 restoration, dashboard styling alignment |
| `54e579da50` | Remove remaining net9 targeting and hosting workarounds |
| `5b70eb648b` | Dialog and mobile-layout parity |
| `5ace6af03b` | Mobile navigation and Manage Data interaction fixes |
| `53d098b960` | Chart-filter state, centralized disabled button recipes, message-bar spacing |
| `0b8fc37fae` | Grid icon sizing, toast colors, submenu gap removal, inline message wrapping |
| `154570bb8b` | Dialog-close ordering and required-field accessibility |
| `0f0d7acdeb` | Trace-waiting progress dialog and polling completion |
| `b96c8482b1` | Cog visibility for cursor-menu lifetime; no HTML anchoring overlay |

For GitHub operations, use the `JamesNK` account, not `jamesnk_microsoft`. Commits, pushes, issue creation, and thread resolution require the relevant user request. Verify `git status`/`git log` after commits and the remote branch hash after pushes; do not report success from empty or ambiguous terminal output.

## Continuation Acceptance Criteria

- The current change is tied to a concrete user request or refreshed PR finding.
- The final code preserves the decisions above unless the user explicitly changes them.
- Focused behavior tests pass and actually exercise the changed path; CSS-sensitive behavior is checked in a real browser.
- Shared styles are checked against adjacent consumers, both themes, and relevant viewport/state transitions.
- Disabled, selected, loading, keyboard-focus, and dismissal states are not inferred from an idle screenshot.
- No unrelated user edits, v4 reference processes, secrets, package baselines, or generated API files are disturbed.
- The close-out states what was implemented, what was verified, what remains unverified, and whether changes are local, committed, pushed, or reflected in review-thread status.