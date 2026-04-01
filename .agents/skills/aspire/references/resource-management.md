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
- If the user says one resource is wedged, use `aspire resource <resource> restart` before escalating to `aspire start`.
- Use `aspire resource <resource> <command>` when the AppHost exposes a resource-specific dashboard or operational command.
