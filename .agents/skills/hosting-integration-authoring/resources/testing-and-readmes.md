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
- `aspire publish` asset behavior separately from `aspire deploy`: publish should produce deterministic, reviewable assets with placeholders/target-native references; deploy should consume or regenerate explicit deploy-time assets, mutate the target, verify provider state, and record outputs.
- Real deploy smoke coverage for new deployment publishers: build/push an image, apply the generated artifact to the target, query the deployed resource status/URL, call the service once, and clean up deployed resources and images.
- Target-specific schema constraints such as resource names, labels, annotations, protocol fields, and IAM defaults when generating artifacts for a new deployment platform.
- The expected deployed access mode. If the target is private by default, smoke tests should call it with the required identity/token; if the integration exposes public access, smoke tests should prove anonymous access only for that explicit opt-in path.
- Production deployment scenarios for new publishers: at least one project resource, one prebuilt container resource, one multi-service app if service references are supported, one secret/reference scenario, one private/default access scenario, and one cleanup or documented external-cleanup path.
- Preflight failure paths for missing CLI, missing auth/context, missing registry access, disabled target APIs/services, and insufficient permissions.
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
- Don't treat serializer snapshots or fake CLI argument tests as proof that a deployment target accepts the artifact.
- Don't stop validation at "the deploy command exited"; check the provider's resource state and perform the expected authenticated or anonymous request.
- Don't collapse publish and deploy tests into one path; a deployment integration can generate correct assets and still fail provider validation, and provider deploy can work while publish assets are incomplete or unreproducible.

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

Deployment target READMEs should also include:

- Required local tools and authentication commands.
- Required cloud/project/subscription/cluster prerequisites, including enabled APIs/services and registry setup.
- The default access mode and how to opt into public access if supported.
- Secret handling limitations or the target-native secret reference APIs.
- Publish asset output locations, deploy-time output locations, and cleanup/destroy expectations.
- The difference between `aspire publish` and `aspire deploy`: what assets are generated, what values remain placeholders, what deploy resolves, and what target mutations deploy performs.
- Known limitations that affect production use, such as unsupported service-to-service references, networking, custom domains, or provider-specific resources.

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
