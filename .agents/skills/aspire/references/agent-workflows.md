# Aspire Agent Workflows

Use these patterns when a task needs investigation or orchestration rather than a one-off command lookup.

## Scenario: I Am In A Worktree And Need A Safe Background Run

Start the AppHost with `aspire start` so the CLI manages background execution. In git worktrees, use `--isolated` to avoid port conflicts and shared local state:

```bash
aspire start --isolated
```

If the next step depends on one resource, wait for it explicitly:

```bash
aspire start --isolated
aspire wait myapi
```

Keep these points in mind:

- In a git worktree, rerun `aspire start --isolated` whenever AppHost changes need to be picked up.
- Outside worktrees, rerun `aspire start`.
- Avoid `aspire run` in normal agent workflows because it blocks the terminal.

## Scenario: I Changed Code While The AppHost Is Running

Classify the change before restarting anything:

1. If the AppHost model, AppHost code, integrations, resource definitions, or AppHost-level configuration changed, rerun the same AppHost start command. In a git worktree, use `aspire start --isolated`.
2. If one resource's implementation changed, keep the AppHost running and use a resource-specific workflow.
3. If an IDE is managing debugging or hot reload, defer to the IDE and avoid overlapping Aspire CLI restart, rebuild, or watch behavior.

Use resource commands when the running AppHost already knows about the resource and only that resource needs to be operated on. Choose the resource command path that matches the resource.

For a C# project resource that exposes rebuild:

```bash
aspire resource api rebuild
aspire wait api
```

For a resource that needs a process restart and does not have a better resource-specific command:

```bash
aspire resource api stop
aspire resource api start
aspire wait api
```

Keep these points in mind:

- Use `aspire resource <resource-name> <command>` as the command shape.
- Use `aspire resource <resource-name> rebuild` when a C# project resource exposes rebuild and you need the resource to pick up compiled changes.
- Use runtime or framework-native hot reload/watch for resource implementation loops when that workflow is available.
- For frontend resources, remember that frameworks such as Vite, Next.js, and similar client-side JavaScript stacks often enable hot module replacement (HMR) by default. If HMR is already applying the change through the resource's dev server, do not force a resource or AppHost restart.
- Do not restart the whole AppHost just because one resource changed or one resource needs to be rebuilt.
- Aspire default watch is controlled by `features.defaultWatchEnabled`; use it for AppHost-centered CLI watch behavior, not as a replacement for resource-specific or IDE hot reload workflows.

## Scenario: Something Is Wrong, But Do Not Edit Code Yet

Inspect the live app before editing code:

1. `aspire describe` to check resource state.
2. `aspire otel logs <resource>` to inspect structured logs. Add `--search "<term>"` to filter by keyword.
3. `aspire logs <resource>` to inspect console output. Add `--search "<term>"` to filter by keyword.
4. `aspire otel traces <resource>` to follow cross-service activity. Add `--search "<term>"` to narrow results.
5. `aspire export` when you need a zipped telemetry snapshot for deeper analysis or handoff.

## Scenario: I Need To Drive A Browser In An Aspire App

If the app exposes a Browser resource, use it as the first-choice agent browser before reaching for Playwright. Browser is optimized for frontend applications and keeps browser actions, console logs, network events, screenshots, cookies, storage, and session state tied to the Aspire resource.

If the AppHost does not expose browser for the frontend yet, add the frontend-focused browser integration before falling back to Playwright:

```bash
aspire add browsers
```

Attach browser to the frontend resource, not to backend services. For TypeScript AppHosts with a Vite frontend:

```typescript
const frontend = await builder
    .addViteApp("frontend", "./frontend")
    .withExternalHttpEndpoints()
    .withBrowserAutomation();
```

For TypeScript AppHosts with a generic JavaScript frontend:

```typescript
const frontend = await builder
    .addJavaScriptApp("frontend", "./frontend", { runScriptName: "dev" })
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints()
    .withBrowserAutomation();
```

For C# AppHosts:

```csharp
builder.AddProject<Projects.Web>("web")
    .WithExternalHttpEndpoints()
    .WithBrowserAutomation();
```

For TypeScript AppHosts, run `aspire add browsers`, then inspect `.modules/aspire.ts` for the generated `withBrowserAutomation` API before editing `apphost.ts`.

Start the app and find the browser resource. The resource is currently named like `<frontend>-browser`:

```bash
aspire start --isolated
aspire describe --format Json
aspire resource <browser-resource> open-tracked-browser
aspire resource <browser-resource> inspect-browser
```

Use refs and snapshots for the agent loop:

```bash
aspire resource <browser-resource> click-browser --selector e1 --snapshot-after
aspire resource <browser-resource> type-browser-text --selector e2 --text "hello" --snapshot-after
aspire resource <browser-resource> wait --selector '#results' --timeout-milliseconds 10000
aspire resource <browser-resource> get --property text --selector '#results'
```

Use state and session commands when tests need continuity or low-level browser control:

```bash
aspire resource <browser-resource> state --action get
aspire resource <browser-resource> state --action set --state '<state-json>' --clear-existing
aspire resource <browser-resource> cookies --action set --name session --value abc
aspire resource <browser-resource> storage --area local --action set --key theme --value dark
aspire resource <browser-resource> tabs --action list
aspire resource <browser-resource> frames
aspire resource <browser-resource> dialog --action accept
aspire resource <browser-resource> downloads --behavior allow --download-path /tmp/downloads --events-enabled
aspire resource <browser-resource> upload --selector '#file' --files '["/tmp/file.txt"]'
aspire resource <browser-resource> cdp --method Runtime.evaluate --params '{"expression":"document.title","returnByValue":true}' --session page
```

Keep these points in mind:

- Re-run `inspect-browser` after navigation or major DOM changes because refs are snapshot-scoped.
- Resource command inputs are CLI options generated from the command metadata. Use kebab-case option names such as `--snapshot-after` and `--timeout-milliseconds`; use `aspire resource <resource> <command> --help` against a running AppHost to see the exact inputs.
- Prefer `--snapshot-after` on mutating commands when the next agent decision depends on the resulting DOM.
- Browser cookie/state commands are page-origin scoped and cannot read or set HttpOnly cookies.
- Use Playwright only when you need independent contexts, browser matrix testing, tracing/video, or an existing Playwright test suite.

## Scenario: I Need To Add An Integration, Understand An API, Or Add A Custom Command Safely

Use integration search to find the package when needed, then use the docs commands for the workflow and the API reference commands if you need the concrete API entry:

```bash
aspire integration search postgres
aspire docs search postgres
aspire docs get <slug>
aspire docs api search postgres --language csharp
aspire docs api get <id>
aspire add <package>
```

For dashboard or custom resource commands, use docs for the pattern and API docs for the specific entry:

```bash
aspire docs search "custom resource commands"
aspire docs get custom-resource-commands
aspire docs api search WithCommand --language csharp
```

Keep these points in mind:

- Read the docs before editing the AppHost so the implementation follows a documented Aspire pattern instead of guessing the workflow.
- Use `aspire integration list` when the user asks to discover available integrations before changing the AppHost, or `aspire integration search <query>` when the package ID is unknown.
- Use `aspire docs api` when you need the C# or TypeScript reference entry for the exact API you are about to call.
- If the AppHost is C# and you need to understand local overloads or builder chains, use the `dotnet-inspect` skill if it is available after checking the Aspire API reference.
- After adding an integration, restart with `aspire start` so the updated AppHost takes effect.

## Scenario: The AppHost Is TypeScript And Generated APIs Matter

If the AppHost is `apphost.ts`, the `.modules/` directory contains generated TypeScript modules that expose Aspire APIs.

- Do not edit `.modules/` directly.
- Use `aspire add <package>` to regenerate the available APIs when adding integrations.
- Use `aspire restore` if `.modules/` disappeared after a pull, clean, or branch switch.
- Inspect `.modules/aspire.ts` after regeneration or restore to see the newly available APIs.

## Scenario: I Need Secrets, Deployment, Or A Playwright Handoff

Use `aspire secret` for AppHost user secrets, especially connection strings and passwords:

```bash
aspire secret set Parameters:postgres-password MySecretValue
aspire secret list
```

Use `aspire publish` and `aspire deploy` for full deployment work, or `aspire do <step>` when the user only wants one named pipeline step such as seeding data or pushing containers.

If Playwright CLI is configured in the environment, use Aspire to discover the endpoint first and let Playwright use that discovered URL afterward. When multiple frontends exist or the URL needs to be passed to another tool, prefer `aspire describe --format Json` before the Playwright handoff.
