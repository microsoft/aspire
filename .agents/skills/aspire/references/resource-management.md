# Resource Management

Use this when the task is scoped to one resource or depends on a specific resource becoming healthy.

## Scenario: Wait For One Resource Before Touching It

Use these commands when the next step depends on one resource being ready, such as before calling an API, opening a frontend, or querying a database.

```bash
aspire wait <resource>
aspire wait <resource> --status up --timeout 60
```

Keep these points in mind:

- Use `aspire wait` before a dependent action when readiness is the real blocker.
- Add `--status` and `--timeout` when the ask calls for an explicit readiness condition rather than a generic wait.
- Treat readiness as a resource-scoped concern; a missing ready signal is not automatically a reason to restart the whole AppHost.

## Scenario: Fix Or Operate On One Resource Without Bouncing The Whole App

Use these commands when the user calls out one resource by name, such as Redis, Postgres, cache, or a single custom resource command.

```bash
aspire resource <resource> start
aspire resource <resource> stop
aspire resource <resource> restart
aspire resource <resource> <command>
```

Keep these points in mind:

- Prefer resource-scoped commands when the task does not require an AppHost-wide restart.
- If the user says one resource is wedged, use a resource-scoped command such as `aspire resource <resource> restart` when available, or stop and start that resource, before escalating to `aspire start`.
- Use `aspire resource <resource> <command>` when the AppHost exposes a resource-specific dashboard or operational command, such as `aspire resource <resource> rebuild` for C# project resources that expose rebuild.

## Scenario: Apply One Resource's Code Change Without Bouncing The Whole App

Use one of these alternatives when the AppHost is already running and a single resource needs to pick up a code or runtime change.

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

- Use the shape `aspire resource <resource-name> <command>`.
- Use `rebuild` for C# project resources when that command is available and the resource needs rebuilt output.
- Use stop/start as an alternative when the resource process needs to restart but the AppHost model did not change.
- Use framework or runtime-native watch, hot reload, or hot module replacement (HMR) for tight resource implementation loops when available.
- Frontend frameworks such as Vite, Next.js, and similar client-side JavaScript stacks often enable HMR by default; if the resource's dev server is already handling the update, do not force a resource or AppHost restart.
- Do not stop or restart the whole AppHost just because one resource changed.
