# App Commands

Use this when the task is about app-level lifecycle, bootstrap, or AppHost-wide maintenance.

## Scenario: Start The App Safely In The Background

Use these commands when the user wants the AppHost running, needs a safe worktree session, or wants to pick up AppHost changes.

```bash
aspire start
aspire start --isolated
aspire stop
```

Keep these points in mind:

- Use `aspire start` for normal background AppHost execution.
- In git worktrees or when another local instance may already be running, use `aspire start --isolated`.
- If the CLI shape may have changed or the skill instructions look older than the local CLI, confirm the exact command form with `aspire --help` or the relevant subcommand help before executing.
- To restart after AppHost changes, rerun the same start command. In a worktree, rerun `aspire start --isolated`.
- Use `aspire stop` only when the ask is explicitly to stop the app or clean up a running AppHost.
- Avoid `aspire run` in normal agent workflows because it blocks the terminal.

## Scenario: Create A New Aspire App Or Add Aspire To An Existing App

Use these commands when the task is to bootstrap Aspire support.

```bash
aspire new
aspire init
aspire init --language typescript
```

Keep these points in mind:

- Use `aspire new` when creating a brand-new Aspire app from scratch.
- Use `aspire init` when adding Aspire to an existing application.
- If you are unsure which arguments are supported by the installed CLI, check `aspire init --help` (or the parent help) before running it.
- If the existing app flow needs a specific AppHost language, choose it explicitly rather than inventing unrelated scaffolding.

## Scenario: Find The Right AppHost Or Refresh AppHost-Wide Support

Use these commands when multiple AppHosts may be running locally, when the AppHost needs an integration, or when local AppHost support files need refresh or restore.

```bash
aspire ps
aspire add <package>
aspire update
aspire restore
```

Keep these points in mind:

- Use `aspire ps` first when you need to discover which AppHost is already running.
- If command arguments may differ across CLI versions, verify the current shape with `aspire <command> --help` before execution.
- Use `aspire add <package>` when the task is to add a supported integration or regenerate AppHost APIs.
- Use `aspire update` when the ask is specifically to refresh AppHost package references through the supported CLI workflow.
- Use `aspire restore` after pulls, cleans, or missing generated files when the AppHost needs its local support restored before running again.
