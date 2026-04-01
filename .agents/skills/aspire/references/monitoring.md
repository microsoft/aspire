# Monitoring

Use this when the task is about inspecting app state, logs, traces, endpoints, or sharable diagnostics.

## Scenario: I Need To Know What Is Running And Where The Endpoints Are

Use these commands when the first job is to inspect current resource state, find URLs, or hand machine-readable app state to another tool.

```bash
aspire describe
aspire resources
aspire describe --apphost <path>
aspire describe --apphost <path> --format Json
```

Keep these points in mind:

- Use `aspire describe` first when you need the current state of the running app before deciding what to do next.
- Use `--apphost <path>` when the workspace has multiple AppHosts or discovery is ambiguous.
- Prefer `--format Json` when another tool or script needs to consume the result, such as a Playwright handoff or endpoint extraction.

## Scenario: Something Is Wrong, But Investigate Before Editing Code

Use these commands when the task is to diagnose behavior in the live app before making code changes.

```bash
aspire otel logs [resource]
aspire otel traces [resource]
aspire otel spans [resource]
aspire otel logs --trace-id <id>
aspire logs [resource]
```

Keep these points in mind:

- Prefer structured telemetry before raw console logs when possible.
- Use `aspire logs` as a secondary console-output view after checking structured telemetry.
- Use the trace-filtered log command when you already have a trace id and want the related log slice.

## Scenario: I Need A Sharable Diagnostics Bundle

Use this command when you need a portable handoff artifact for deeper analysis or for another person to inspect offline.

```bash
aspire export [resource]
```

Keep this point in mind:

- Use `aspire export` when you need a sharable bundle of telemetry and resource state.
