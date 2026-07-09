# Archetype: credentials and secrets provider integration

Use this archetype for integrations that broker secrets or credentials from an external provider into the app model, deployment pipeline, or consuming resources.

Examples:

- Secret manager resources that create or adopt provider-side projects/secrets.
- Integrations that turn external provider secrets into `ParameterResource`-like values.
- Build or deployment helpers that need provider access tokens while keeping consuming workloads secret-safe.

## Resource shape

DO:

- Decide whether the provider resource represents a live local service, an external/cloud reference, or a provisioning/deployment control plane.
- Prefer non-container `Resource` types when the integration only models an external provider and does not run a local service.
- Model managed secret entries as child resources when the provider owns individual secrets.
- Keep provider URLs, organization/project IDs, access tokens, and cache paths as resource state or annotations; constructors should not call the provider.
- Use `ParameterResource`, `ReferenceExpression`, and provider-specific value providers to keep secrets late-bound.

DON'T:

- Don't model an external secret provider as a container-backed service unless a container actually runs locally.
- Don't resolve or fetch secrets during app-model construction.
- Don't leak access tokens or managed secret values into resource snapshots, logs, generated files, or manifests.

## Provisioning and state

DO:

- Use pipeline steps or lifecycle hooks for provider mutations such as creating projects, creating/updating secrets, or recording provider IDs.
- Make provider operations idempotent: adopt existing projects/secrets when configured to do so, and track provider identifiers when required.
- Keep local bookkeeping caches deterministic and documented, and let users override cache locations for CI/shared scenarios.
- Clearly separate management credentials from workload credentials.
- Surface provider errors with actionable messages that include resource names and operation names but not secret values.

DON'T:

- Don't silently recreate secrets when provider identifiers are missing; explain whether the integration is adopting, creating, or updating provider state.
- Don't assume provider-side names are unique unless the provider guarantees uniqueness.

## Relationships and waits

DO:

- Add explicit relationships to parameter resources, external provider endpoints, and resources whose values are used by the secret provider.
- Use `WaitFor` only when a local resource or external-service resource must be ready before provider operations can run.
- Document whether managed secrets are available during run, publish, deploy, or all modes.

DON'T:

- Don't hide provider dependencies behind name conventions.
- Don't make workloads wait for the secret provider if they only consume already-resolved environment values.
