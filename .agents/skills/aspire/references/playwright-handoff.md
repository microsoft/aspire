# Playwright Handoff

Use this when Playwright CLI is already configured and the next step is browser testing against a running Aspire app.

Before handing off to Playwright, check whether the AppHost has a Browser resource for the frontend. Browser is optimized for frontend applications, exposes an agent browser interface through `aspire resource <browser-resource> <command> [options]`, and keeps browser console/network telemetry attached to the Aspire resource.

If the AppHost does not have browser yet, run the frontend-focused browser integration alias and attach it to the frontend before falling back to Playwright:

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

Prefer Browser resource commands when the task can be handled by the tracked browser:

```bash
aspire resource <browser-resource> open-tracked-browser
aspire resource <browser-resource> inspect-browser
aspire resource <browser-resource> click-browser --selector e1 --snapshot-after
aspire resource <browser-resource> fill-browser --selector '#email' --value 'user@example.com' --snapshot-after
aspire resource <browser-resource> wait --selector '#results' --timeout-milliseconds 10000
aspire resource <browser-resource> get --property text --selector '#results'
aspire resource <browser-resource> state --action get
aspire resource <browser-resource> storage --area local --action set --key theme --value dark
aspire resource <browser-resource> cookies --action get
aspire resource <browser-resource> tabs --action list
aspire resource <browser-resource> cdp --method Target.getTargets --params '{}' --session browser
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
- If a Browser resource exists, try its resource commands first so logs, network events, screenshots, and state changes stay correlated with Aspire telemetry.
- `inspect-browser` returns element refs such as `e1`; use refs quickly and re-run `inspect-browser` after navigation or major DOM changes.
- Resource command inputs are CLI options generated from the command metadata. Use kebab-case option names such as `--snapshot-after` and `--timeout-milliseconds`; run `aspire resource <resource> <command> --help` against a running AppHost to confirm the exact inputs.
- Mutating commands commonly support `--snapshot-after` to return a fresh page snapshot for agent verification.
- Browser `state`, `cookies`, and `storage` operate in the active page origin and only expose page-visible cookies; use Playwright browser contexts when HttpOnly cookies or multi-origin storage state are required.
- Use the raw `cdp` command as the escape hatch for low-level browser protocol operations before leaving Aspire.
- Prefer `aspire describe --format Json` when the URL needs to be consumed by a script or passed to another tool.
- Use `--apphost <path>` when multiple AppHosts exist and the user is asking about one specific app.
- Do not guess frontend endpoints without first consulting Aspire state.
- If multiple frontends exist, use Aspire state to disambiguate which URL Playwright should use.
