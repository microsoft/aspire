# Security, secrets, and identity

Hosting integrations often handle credentials, generated files, deployment output, and cloud role assignments. Default to least privilege and never materialize secrets unless the target system requires it.

## Parameters and secrets

DO:

- Use `ParameterResource` for passwords, API keys, tokens, connection-string secrets, and generated credentials.
- Use `ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter` for generated passwords.
- Mark user-provided secret parameters as secret.
- Pass secrets through `ReferenceExpression`, environment callbacks, parameter references, BuildKit secrets, or deployment-secret mechanisms.
- Keep secrets late-bound so they do not appear in app model logs or generated code.

DON'T:

- Don't generate random secrets inside publish callbacks.
- Don't write secrets to generated Dockerfiles, YAML, Bicep, README examples, or logs.
- Don't concatenate secret values into plain strings when a reference expression can preserve secrecy.
- Don't use deterministic or hardcoded default passwords.

## Generated artifacts

Generated deployment or config artifacts must not leak secrets.

DO:

- Use placeholders, parameter references, or secret mounts for generated artifacts.
- Use the deployment target's native secret reference shape for deploy-time manifests, such as Kubernetes `secretKeyRef`, cloud secret-manager references, or platform-managed secret resources.
- Use BuildKit secret mounts for private package/module credentials in generated Dockerfiles.
- Remove temporary credential files in the same Docker layer when a tool requires a credential file.
- Keep generated examples redacted.

DON'T:

- Don't persist credentials in Docker layers.
- Don't emit `.env`, Compose, Kubernetes, Bicep, or appsettings content with raw secret values.
- Don't treat deploy-time generated manifests as safe just because they are temporary; anything written under the publish output can be archived, logged, or inspected later.
- Don't put access tokens in command-line arguments when an environment variable, secret file, or parameter reference works.

## Azure identity and RBAC

DO:

- Prefer managed identity and RBAC over access keys when the Azure service supports it.
- Assign least-privilege built-in roles required by consumers.
- Scope role assignments to the smallest practical resource.
- Treat existing Azure resources as read-only intent. Do not add creation-only auth or provisioning mutations to them.
- Use private endpoints or network restrictions when the integration supports private networking and the user opts in.

DON'T:

- Don't enable public network access by default when a private endpoint configuration requires denial.
- Don't grant broad owner/contributor roles when a data-plane role is sufficient.
- Don't re-enable shared key access unless there is a service-specific reason.

## External services

DO:

- Make live external credentials explicit prerequisites.
- Expose common production identity settings as first-class APIs, such as service account/managed identity selection, role bindings, and public/private ingress controls.
- Use the target's native secret store/reference shape for deployment manifests instead of materializing secret values.
- Validate least-privilege permissions separately from resource mutation when the target supports non-mutating IAM/probe calls.
- Preserve least-privilege/private-by-default access for deployed resources unless the integration has an explicit public-access API.
- Document and test the identity required for smoke validation when the deployed endpoint is private by default.
- Keep health checks side-effect-free and avoid expensive/rate-limited calls.
- Avoid validating external credentials during `aspire start` unless the user explicitly opted into that behavior.

DON'T:

- Don't call chargeable or mutating APIs as part of ordinary app-model construction.
- Don't assume CI has live external-service credentials.
- Don't make deployed endpoints anonymously reachable just to simplify smoke tests.
- Don't provide a blanket "make public" helper that hides broad IAM changes; make the security impact explicit in API names, docs, and tests.

## Logs and diagnostics

DO:

- Redact credentials and tokens in log messages and exception messages.
- Include resource names and operation names in errors without including secret values.
- Log enough context to diagnose missing credentials, missing role assignments, or denied access.

DON'T:

- Don't log connection strings that include credentials.
- Don't include secret values in `DistributedApplicationException` messages.
