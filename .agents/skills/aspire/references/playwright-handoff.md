# Playwright Handoff

Use this when Playwright CLI is already configured and the next step is browser testing against a running Aspire app.

Before handing off to Playwright, check whether the AppHost has a Browser Automation resource for the frontend. Browser Automation is optimized for frontend applications, exposes an agent browser interface through `aspire resource <browser-automation-resource> <command> ...`, and keeps browser console/network telemetry attached to the Aspire resource.

If the AppHost does not have browser automation yet, run the frontend-focused browser integration alias and attach it to the frontend before falling back to Playwright:

```bash
aspire add browsers
```

TypeScript AppHost with Vite frontend:

```typescript
const frontend = await builder
    .addViteApp("frontend", "./frontend")
    .withExternalHttpEndpoints()
    .withBrowserAutomation();
```

TypeScript AppHost with a generic JavaScript frontend:

```typescript
const frontend = await builder
    .addJavaScriptApp("frontend", "./frontend", { runScriptName: "dev" })
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints()
    .withBrowserAutomation();
```

C# AppHost:

```csharp
builder.AddProject<Projects.Web>("web")
    .WithExternalHttpEndpoints()
    .WithBrowserAutomation();
```

Prefer Browser Automation resource commands when the task can be handled by the tracked browser:

```bash
aspire resource <browser-automation-resource> open-tracked-browser
aspire resource <browser-automation-resource> inspect-browser
aspire resource <browser-automation-resource> click-browser e1 snapshotAfter=true
aspire resource <browser-automation-resource> fill-browser '#email' 'user@example.com' snapshotAfter=true
aspire resource <browser-automation-resource> wait selector='#results' timeoutMilliseconds=10000
aspire resource <browser-automation-resource> get text '#results'
aspire resource <browser-automation-resource> state get
aspire resource <browser-automation-resource> storage local set theme dark
aspire resource <browser-automation-resource> cookies get
aspire resource <browser-automation-resource> tabs list
aspire resource <browser-automation-resource> cdp Target.getTargets '{}' browser
```

Use Playwright when the task needs capabilities outside the tracked browser resource, such as independent browser contexts, cross-browser matrix testing, tracing/video, or a test suite already written in Playwright.

## Scenario: I Need The Right Frontend URL Before Browser Testing

Use these commands when the task is to discover the live frontend endpoint from Aspire state and then hand that URL to Playwright.

```bash
aspire describe --format Json
aspire describe --apphost <path> --format Json
playwright-cli --help
```

Keep these points in mind:

- Aspire discovers the endpoint first; Playwright uses the discovered endpoint after the handoff.
- If a Browser Automation resource exists, try its resource commands first so logs, network events, screenshots, and state changes stay correlated with Aspire telemetry.
- `inspect-browser` returns element refs such as `e1`; use refs quickly and re-run `inspect-browser` after navigation or major DOM changes.
- Mutating commands commonly support `snapshotAfter=true` to return a fresh page snapshot for agent verification.
- Browser Automation `state`, `cookies`, and `storage` operate in the active page origin and only expose page-visible cookies; use Playwright browser contexts when HttpOnly cookies or multi-origin storage state are required.
- Use the raw `cdp` command as the escape hatch for low-level browser protocol operations before leaving Aspire.
- Prefer `aspire describe --format Json` when the URL needs to be consumed by a script or passed to another tool.
- Use `--apphost <path>` when multiple AppHosts exist and the user is asking about one specific app.
- Do not guess frontend endpoints without first consulting Aspire state.
- If multiple frontends exist, use Aspire state to disambiguate which URL Playwright should use.
