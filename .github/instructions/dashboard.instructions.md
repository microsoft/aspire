---
applyTo: "src/Aspire.Dashboard/**/*.{cs,razor,js}"
---

# Dashboard agent instructions

## Reviewing

- Dashboard subscription/watch callbacks can run concurrently; protect shared mutable state with locking or concurrent collections.
- Prefer FluentUI for standard interactive controls. Raw HTML is fine when semantics, performance, or UX require it; if working around a FluentUI limitation, cite the FluentUI issue.
- Use `ViewportInformation.IsDesktop` / `IsUltraLowHeight` cascading parameter for responsive layout; throttle (not debounce) resize events to avoid excessive re-renders of the entire component tree.
- Use `@onclick:stopPropagation="true"` on interactive elements inside `FluentDataGrid` rows that have row-click handlers to prevent unintended navigation.
- Prefer JS interop for browser-only, latency-sensitive interactions (clipboard, global DOM listeners). If you register a persistent JS listener, keep a handle and unregister in `DisposeAsync`.
- For high-throughput log/trace/metric streams with a fixed cap, use `CircularBuffer<T>` instead of repeatedly removing the first item from a `List<T>`.
- For bounded channels feeding one consumer, prefer `BoundedChannelFullMode.DropOldest` and set `SingleReader = true`.
- Use `FormatHelpers` for culture-aware date/time/number display. Reserve invariant formatting for intentionally fixed diagnostic formats.
- Localize user-visible dashboard text with resource-backed localizers. Prefer typed localizers and `nameof` keys when practical, but existing model/helpers also generate localized UI text.

### Blazor components

- Avoid `@code` blocks and substantial C# logic in `.razor` files. Keep `.razor` files focused on markup, directives, and simple binding or event expressions; put component state, lifecycle methods, event handlers, and other logic in the matching `.razor.cs` code-behind partial class for better IDE and compiler support.
- Declare injected component dependencies as public, required, init-only properties:

	```csharp
	[Inject]
	public required IDashboardClient DashboardClient { get; init; }
	```

- `public` keeps dependencies visible to component and test infrastructure, `required` expresses that the component cannot operate without the service, and `init` prevents reassignment after component activation.
- Do not use non-public injected properties, mutable `set` accessors, or null-forgiving initializers such as `= null!;`. These weaken compile-time validation and hide missing dependencies when components are constructed in tests.

## Automated local development testing

### Browser verification with Playwright

- Start the exact AppHost using the normal lifecycle workflow, then run `aspire wait aspire-dashboard --apphost <apphost-path> --non-interactive` before opening Playwright. The first TestShop launch can spend time building before it appears in `aspire ps`; an editor "started" response means the launch was accepted, not that the dashboard is ready. Do not start a second AppHost or guess a port while the first is building.
- Choose one local-development authentication approach:
  - **Authenticated (preferred for an already-running AppHost):** Read `aspire ps --format Json --non-interactive` in the same process as the Playwright code. Select the entry whose `appHostPath` is the exact AppHost path and use its `dashboardUrl` for `page.goto(dashboardUrl)`. The URL may contain `/login?t=<token>`, which establishes the browser session. Keep it in memory: do not print the raw `aspire ps` JSON, log browser navigation URLs, include the URL in screenshots or accessibility dumps, or commit it. Read a fresh `dashboardUrl` after a dashboard restart.
  - **Anonymous local AppHost:** Set `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` **before starting** the AppHost. For a CLI-managed launch, set it in the same shell as `aspire start`; for an editor-managed launch, set it in the AppHost's launch environment before starting through the editor:

    ```powershell
    $env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS = "true"
    aspire start --apphost <apphost-path> --non-interactive
    Remove-Item Env:ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS
    ```

    Use this only on a trusted local development machine. `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` does **not** disable dashboard authentication. Get the URL for this AppHost from `aspire ps --format Json --non-interactive`; in anonymous mode, `dashboardUrl` is the base URL without a login token. Switching authentication modes requires an AppHost restart, so prefer the existing authenticated session for a quick loop.
- Open the URL in Playwright and assert both page loading and the actual changed behavior. A visible Resources heading alone is not proof that resource data has loaded: wait for a known resource row (or another relevant UI state) before interacting with it. For example, with `page`, `expect`, and `dashboardUrl` available:

	```typescript
	await page.goto(dashboardUrl);
	await expect(page.getByRole('heading', { name: 'Resources' })).toBeVisible();
	await expect(page.getByRole('row').filter({ hasText: 'basketcache' })).toBeVisible();
	```

  Replace `basketcache` with a resource in the selected AppHost. For a local HTTPS certificate not trusted by the automation browser, configure that *local* Playwright context with `ignoreHTTPSErrors: true`.

### Rebuilding the dashboard in a running AppHost

- When the dashboard is running as the `aspire-dashboard` **project** resource (`Projects.Aspire_Dashboard`) and **only the dashboard project changed**, reuse the AppHost and rebuild that resource. The fastest reliable automated loop is: verify the existing page, edit, run the CLI Rebuild command to completion, wait for health, then reload/reopen the dashboard in Playwright and assert the changed UI. In TestShop, simply editing Razor markup did not update the running page; rebuilding applied the edit without restarting the AppHost.

	```powershell
	aspire resource aspire-dashboard rebuild --apphost <apphost-path> --non-interactive
	aspire wait aspire-dashboard --apphost <apphost-path> --non-interactive
	```

- Use the exact running AppHost path when multiple AppHosts exist. Rebuild stops the dashboard, builds, and restarts it; the browser briefly disconnects. Check the rebuild command's exit status and build output **before** waiting for health. Then fetch the current dashboard URL, reload or revisit it in Playwright, and assert the specific new behavior. Do not interpret an old page still showing the pre-edit markup as success.
- The dashboard UI also offers **Rebuild** in the `aspire-dashboard` resource's **Actions** menu. That resource is hidden by default: enable **View options > Show hidden resources** first. UI-triggered rebuild is asynchronous; a simultaneous `aspire wait` can see the temporary Stopped state and fail before the build completes. Wait for the action/build to finish (or check the `aspire-dashboard-rebuilder` state and exit code with `aspire describe --include-hidden`), then wait for dashboard health and verify in Playwright. The CLI command is preferable for agents because it reports build failure and completion directly. If a build fails or the new UI is absent, inspect `aspire logs aspire-dashboard --apphost <apphost-path> --tail 80 --non-interactive` rather than repeatedly restarting the whole AppHost.
- The built-in dashboard executable is not a project resource and does not expose this Rebuild command. If the AppHost, `Aspire.Hosting`, or **any other project** changed, restart the AppHost through the normal lifecycle workflow instead of only rebuilding the dashboard; the running AppHost will not load those changes from a dashboard rebuild.
