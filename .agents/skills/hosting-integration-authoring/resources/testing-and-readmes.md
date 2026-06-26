# Testing and READMEs

Integration changes should prove the app model, run behavior, publish/deploy behavior, generated artifacts, and documentation stay consistent.

## Tests

DO test:

- Resource type and name.
- Expected annotations.
- Endpoint names, schemes, target ports, and host-port behavior.
- Container image, tag, and registry annotations.
- Health check registration and keys.
- Real container startup, credential-enforced readiness, and a simple protocol operation for container-backed services when practical.
- Data volume and bind-mount persistence with the actual service when the integration exposes persistence helpers; preserve equivalent functional coverage when porting from another implementation.
- Connection string expressions and connection properties.
- Parent-child resource registration and physical-name defaults.
- Run-mode-only resources are absent from publish manifests.
- Publish-only environment/deployment resources are hidden from run mode.
- `RunAsEmulator`, `RunAsContainer`, `RunAsExisting`, `PublishAsExisting`, and `AsExisting` mode behavior.
- Generated manifests, Bicep, Docker Compose, Kubernetes YAML, or Dockerfiles with snapshots when shape matters.
- Polyglot exports when exported API shape changes, including analyzer diagnostics and generated `.d.ts` signatures.
- Controller/reconciler command serialization, conflict detection, command state transitions, cancellation, drift coalescing, and per-resource completion behavior when the integration owns shared external state.

DON'T:

- Don't use live external services in ordinary unit tests.
- Don't drop functional coverage just because the hosting package avoids a client dependency; use raw HTTP/protocol calls when that keeps the hosting integration dependency-free.
- Don't rely on log text for readiness when structured readiness is available.
- Don't use fixed ports in tests unless the test specifically verifies fixed-port behavior.
- Don't mutate static environment or global current directory without cleanup.
- Don't only assert absence; verify the full relevant output shape.
- Don't use live cloud or external services for ordinary controller/reconciler unit tests; fake the controller's external client/provisioner and exercise the queue/command paths directly.

## Multi-language validation

When APIs are exported with ATS metadata, validate the generated SDK shape, not just the C# compile.

DO:

- Enable the integration analyzer for exported integration projects.
- Keep `ASPIREEXPORT*` diagnostics clean.
- Test with a TypeScript AppHost that references the integration `.csproj` in `aspire.config.json`.
- Run `aspire restore` or `aspire run` to generate `.aspire/modules/`.
- Inspect generated `.d.ts` signatures, imports, DTO shapes, callback context accessors, property accessors, and JSDoc.
- Exercise the generated API in `apphost.mts` when the export shape is new or non-trivial.

DON'T:

- Don't assume a C# overload set projects cleanly.
- Don't ship exported APIs without checking generated member names and capability IDs.
- Don't document TypeScript usage until the generated signature has been inspected.

## README content

Hosting integration READMEs should focus on AppHost usage, not consuming-app dependency injection.

Required structure:

1. `# {Technology} hosting integration`
2. Short description starting with `Use this integration to model, configure, and orchestrate...`
3. `## Getting started` with `aspire add Aspire.Hosting.{Technology}`
4. `## Usage example` showing resource creation and `WithReference`
5. `## Connection Properties` when the resource exposes connection properties
6. `## Additional documentation`
7. `## Feedback & contributing`
8. Trademark notice if required

Usage examples:

- Show the minimal common AppHost path.
- Include C#.
- Include TypeScript when the APIs are exported for TypeScript.
- Use variable names that match the technology.
- Show child resources such as `.AddDatabase("db")` when they are the primary usage path.

Connection property tables:

- Put one table per resource shape when parent and child resources differ.
- Include property names exactly as emitted.
- Include URI/JDBC formats.
- Explain that properties become environment variables named `[RESOURCE]_[PROPERTY]`, for example `DB_URI`.

DON'T:

- Don't document consuming-app DI setup in hosting READMEs.
- Don't describe generic health checks, telemetry, or observability unless the integration has unusual AppHost behavior.
- Don't invent TypeScript examples for C#-only or non-exported APIs.
