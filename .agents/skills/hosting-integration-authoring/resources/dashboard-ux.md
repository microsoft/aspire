# Dashboard UX

Hosting integrations shape what users see in the Aspire dashboard. Good dashboard UX makes resources understandable without exposing implementation details.

## Icons and display

DO:

- Set an icon with `WithIconName` when a clear existing icon matches the resource.
- Use names and relationships that make parent-child and companion resources clear.
- Keep hidden setup/deployment-only resources out of the run model or manifest as appropriate.

DON'T:

- Don't show internal setup, deployment-only, or implementation resources as first-class dashboard resources unless users need to act on them.
- Don't use misleading icons or generic names when a clearer resource identity exists.

## URLs

DO:

- Expose primary user-facing URLs.
- Use `WithUrlForEndpoint` to adjust display text or display location.
- Put diagnostic, health, metrics, or secondary URLs in details-only display.
- Expose admin companion URLs when users are expected to open them.

DON'T:

- Don't flood the resource summary with internal endpoints.
- Don't expose health check endpoints as primary app URLs.

## Resource commands

Resource commands are user actions. They should be safe, clear, cancellable, and observable.

DO:

- Use command names and display names that describe the action.
- Validate command preconditions and return clear disabled/unavailable states when possible.
- Honor cancellation tokens.
- Log useful action progress to resource logs.
- Avoid commands that require hidden global state.
- For controller/reconciler integrations, derive command enabled/disabled/hidden state from the controller's active and queued operation state.
- Keep read-only diagnostic commands available during mutating operations when they help recovery.
- Return structured command results for operations that agents or users need to inspect.

DON'T:

- Don't add destructive commands without clear naming and safeguards.
- Don't hide command failures behind success-shaped results.
- Don't log secrets from command arguments or results.
- Don't rely on dashboard command disabling as the only concurrency guard; enforce conflicts in the controller too.

## Notifications and logs

DO:

- Use resource notifications for state transitions users need to see.
- Use resource logger services for generated setup or command logs.
- Keep logs actionable and redact secrets.
- For synthetic/facade resources, publish clear initial, starting, running, and stopped states because there is no DCP process to do it automatically.
- Mark URLs inactive when a manually managed owner resource stops.

DON'T:

- Don't emit noisy informational logs for every callback when they do not help users.
- Don't complete resource logs early if setup work is still running.
- Don't leave dashboard URLs active for endpoints that are no longer forwarded or reachable.

## Admin companions

Admin/dev companions should feel attached to their parent service.

DO:

- Add parent/custom relationships.
- Use clear companion names.
- Exclude companions from publish/deploy output unless intentionally supported.
- Prefer singleton-style companion behavior when the tool manages multiple parent instances.

DON'T:

- Don't make users discover an admin UI by inspecting a random standalone container.
