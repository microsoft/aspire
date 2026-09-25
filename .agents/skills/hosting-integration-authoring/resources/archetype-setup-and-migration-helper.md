# Archetype: setup and migration helper integration

Use this archetype for resources that prepare other resources or apply artifacts rather than running a long-lived service: database migrations, schema deployment, package/tool installation, code generation, model pulls, seed data, or one-shot setup commands.

Examples:

- Database migration tools such as Flyway.
- SQL project or DACPAC deployment helpers.
- Language/package setup siblings such as `npm install`, `go mod tidy`, `cargo fetch`, Maven/Gradle builds, or Perl module installation.
- Model setup resources such as pulling an Ollama model before an app starts.

## Lifecycle shape

DO:

- Decide whether the helper is run-only, publish/deploy-only, or dual-mode.
- Model run-only setup as a sibling resource that the target resource waits for with `WaitForCompletion`.
- Mark run-only helpers `.ExcludeFromManifest()`.
- Use predictable helper names such as `{resource}-install`, `{resource}-migrate`, or `{resource}-model-pull`.
- Make helper creation idempotent when multiple fluent calls can request the same setup resource.
- Surface setup status through normal resource state, resource logs, and clear exceptions.

DON'T:

- Don't perform setup side effects in resource constructors.
- Don't hide long-running setup inside a health check.
- Don't rely on user call order when multiple setup helpers can compose.
- Don't publish run-only setup helpers into deployment artifacts.

## Database and service-state setup

DO:

- Run service-state setup after the target service is reachable and healthy.
- For one-shot migration/deployment resources, explicitly model the target database/service dependency.
- Use `WaitFor` or `WaitForCompletion` so workloads do not start before required setup has completed.
- Make setup idempotent or provide an explicit skip/already-applied mechanism when reruns are expected.
- Keep deployment credentials secret and late-bound.

DON'T:

- Don't create databases, queues, schemas, or indexes as part of plain connection-string construction.
- Don't assume a migration helper should always run in publish/deploy; deployment targets may require a different artifact or pipeline step.

## Artifact inputs

DO:

- Treat paths to migration folders, DACPACs, publish profiles, scripts, or generated files as user input.
- Resolve relative paths against the AppHost or documented project root consistently.
- Normalize paths for the current platform.
- Validate required files early when the helper cannot run without them.
- Use polyglot-friendly overloads for path-based configuration when generic metadata types or callbacks are C#-only.

DON'T:

- Don't use C# project metadata-only overloads as the only API shape when the integration exports polyglot APIs.
- Don't serialize local absolute paths into publish/deploy artifacts unless the target explicitly consumes local files.
