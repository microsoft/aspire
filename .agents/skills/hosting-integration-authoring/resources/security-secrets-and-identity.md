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
- Use BuildKit secret mounts for private package/module credentials in generated Dockerfiles.
- Remove temporary credential files in the same Docker layer when a tool requires a credential file.
- Keep generated examples redacted.

DON'T:

- Don't persist credentials in Docker layers.
- Don't emit `.env`, Compose, Kubernetes, Bicep, or appsettings content with raw secret values.
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
- Keep health checks side-effect-free and avoid expensive/rate-limited calls.
- Avoid validating external credentials during `aspire start` unless the user explicitly opted into that behavior.

DON'T:

- Don't call chargeable or mutating APIs as part of ordinary app-model construction.
- Don't assume CI has live external-service credentials.

## Logs and diagnostics

DO:

- Redact credentials and tokens in log messages and exception messages.
- Include resource names and operation names in errors without including secret values.
- Log enough context to diagnose missing credentials, missing role assignments, or denied access.

DON'T:

- Don't log connection strings that include credentials.
- Don't include secret values in `DistributedApplicationException` messages.
