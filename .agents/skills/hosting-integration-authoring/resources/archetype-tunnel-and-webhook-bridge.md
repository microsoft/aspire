# Archetype: tunnel and webhook bridge integration

Use this archetype for local tools that connect external systems to local Aspire resources: tunneling services, webhook forwarders, callback bridges, and local CLIs that expose or forward endpoints.

Examples:

- Dev Tunnels resources that host a local tunnel executable and expose public endpoint resources.
- Ngrok-style tunnels that expose local endpoints to public URLs.
- Stripe CLI-style webhook listeners that forward external events to local app endpoints.

## Shape and lifecycle

DO:

- Treat tunnel/webhook bridges as run-only unless there is a real deployment target story.
- Call `.ExcludeFromManifest()` for local-only bridges.
- Model the bridge as a resource so it has logs, status, endpoints, and dashboard visibility.
- Use explicit APIs such as `WithTunnelEndpoint` or `WithListen` to select which resource endpoint is exposed or used as the forwarding target.
- Validate user-provided URLs, endpoint names, ports, and auth tokens at API boundaries.
- Make bridge resources wait for the target resource when forwarding requires the target to be ready.
- If the bridge exposes one public endpoint per target endpoint, model those endpoints as child or facade resources rather than overloading the owner resource with many unrelated URLs.
- Read `custom-lifecycle-and-facade-resources.md` when the bridge creates resources whose lifecycle, endpoints, or dashboard state are driven by the bridge process or an external service.
- When centralizing bridge start/ready/stopped behavior, prefer the stateless lifecycle-orchestrator variant in `archetype-controller-reconciler.md`. Add a serialized queue only if the bridge gains shared mutable controller state, command/cancel workflows, or drift/reconcile behavior.

DON'T:

- Don't classify tunnel/webhook bridges as external cloud references; they are local run resources that connect to external services.
- Don't expose every endpoint by default unless the bridge is explicitly designed to do that.
- Don't include local tunnels, webhook listeners, or public callback URLs in publish manifests by default.
- Don't globally serialize independent tunnel resources just because they use the same CLI or login flow; use a narrower concurrency primitive for the shared part.

## Endpoint handling

DO:

- Use endpoint references to select the target endpoint and preserve app-model structure.
- Read allocated endpoint values only from run-mode lifecycle callbacks where allocation has happened.
- Account for host/container networking differences. For example, a tunnel container may need `host.docker.internal` to reach a host-bound local endpoint on Windows or macOS.
- Provide dashboard URLs for bridge control UIs and, when available, discovered public tunnel URLs.
- Return `EndpointReference` values for public bridge URLs so consumers can use normal environment and service discovery flows.

DON'T:

- Don't use host-process `localhost` blindly from inside a container.
- Don't read `EndpointReference.Host`, `Port`, or `Url` in publish-mode callbacks.
- Don't require consumers to copy public URLs from logs or the dashboard.

## Runtime value extraction

Some bridge tools only reveal runtime values such as webhook signing secrets or public tunnel URLs through stdout/stderr.

DO:

- Prefer documented files, APIs, or command options over log parsing when available.
- If log parsing is required, keep it run-only and bounded.
- Parse only documented or observed raw formats; include a comment with an example of the raw line being parsed.
- Add cancellation and timeout behavior so missing output does not hang startup indefinitely.
- Redact extracted secrets in logs and exceptions.
- Expose extracted secrets through deferred values or environment callbacks so consumers can wait for the bridge.

DON'T:

- Don't parse logs in constructors.
- Don't treat a missing extracted value as success when consumers require it.
- Don't write extracted secrets into publish/deploy artifacts.

## Generated bridge configuration

DO:

- Generate tunnel/proxy configuration from `OnBeforeResourceStarted` or another pre-start lifecycle hook when the container/CLI needs the file before launch.
- Use deterministic paths under an AppHost-owned tool folder unless the user explicitly supplies a path.
- Mount generated configuration into the container and pass explicit command-line arguments to consume it.

DON'T:

- Don't generate config files during app-model construction.
- Don't leave generated config in ambiguous temp locations that are hard for users to inspect.
