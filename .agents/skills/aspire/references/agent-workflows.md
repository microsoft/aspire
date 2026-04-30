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

## Scenario: Something Is Wrong, But Do Not Edit Code Yet

Inspect the live app before editing code:

1. `aspire describe` to check resource state.
2. `aspire otel logs <resource>` to inspect structured logs.
3. `aspire logs <resource>` to inspect console output.
4. `aspire otel traces <resource>` to follow cross-service activity.
5. `aspire export` when you need a zipped telemetry snapshot for deeper analysis or handoff.

## Scenario: I Need To Add An Integration, Understand An API, Or Add A Custom Command Safely

Use the docs commands first for the workflow, then use the API reference commands if you need the concrete API entry:

```bash
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
