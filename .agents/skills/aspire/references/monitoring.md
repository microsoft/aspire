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
aspire otel logs [resource] --format Json
aspire otel traces [resource] --format Json
aspire otel spans [resource] --format Json
aspire otel logs --trace-id <id> --format Json
aspire logs [resource]
```

Keep these points in mind:

- Prefer structured telemetry before raw console logs when possible.
- Use `aspire logs` as a secondary console-output view after checking structured telemetry.
- Use the trace-filtered log command when you already have a trace id and want the related log slice.
- Prefer `--format Json` when another tool or script needs to consume the result, such as a Playwright handoff or endpoint extraction.
- `[resource]` is optional. Include it to filter results to a single resource; omit it to see all resources.

## Scenario: I Need A Sharable Diagnostics Bundle

Use this command when you need a portable handoff artifact for deeper analysis or for another person to inspect offline.

```bash
aspire export [resource]
```

Keep this point in mind:

- Use `aspire export` when you need a sharable bundle of telemetry and resource state.

## Dashboard Links

Commands like `aspire describe`, `aspire otel logs`, `aspire otel traces`, and `aspire otel spans` may include dashboard URLs in their JSON output. Only use URLs that are explicitly returned by these commands — do not construct dashboard URLs yourself.

When a dashboard link is returned alongside a resource or telemetry entry, make the resource name, trace ID, or span ID a clickable markdown link using the returned URL.

## Displaying Resources

When showing resource state to the user, display the state text with a circle emoji prefix:

- 🟢 Running, healthy
- 🟡 Starting, waiting
- 🔴 Failed, error, unhealthy
- ⚪ Stopped, exited

Link resource names to their dashboard page when the dashboard URL is known.

## Displaying Telemetry

When showing structured logs, prefix each entry with an emoji matching the log level:

- 🔴 Error / Critical
- 🟡 Warning
- 🔵 Information
- ⚪ Debug / Trace

Link resource names to their dashboard resource page. When trace IDs are present, display the first 7 characters and link that value to the full trace detail page.

When showing traces or spans, use 🟢 for success/unset status and 🔴 for error status. Display only the first 7 characters of trace and span IDs, and link those values to their dashboard detail pages.
