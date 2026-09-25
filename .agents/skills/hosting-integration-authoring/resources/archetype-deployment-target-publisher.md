# Archetype: deployment target or publisher integration

Use this archetype for integrations that generate or customize deployment artifacts: Docker Compose, Kubernetes, Azure Container Apps, or similar targets.

When the integration applies artifacts to a real target or owns infrastructure lifecycle, also read `deployment-production-readiness.md`.

Representative examples:

- `src/Aspire.Hosting.Docker/DockerComposeEnvironmentExtensions.cs`
- `src/Aspire.Hosting.Docker/DockerComposeServiceExtensions.cs`
- `src/Aspire.Hosting.Kubernetes/KubernetesEnvironmentExtensions.cs`
- `src/Aspire.Hosting.Kubernetes/KubernetesServiceExtensions.cs`
- `src/Aspire.Hosting.Azure.AppContainers/*Extensions.cs`

## Resource shape

Deployment targets usually have:

- An environment resource, for example `DockerComposeEnvironmentResource`, `KubernetesEnvironmentResource`, or `AzureContainerAppEnvironmentResource`.
- Infrastructure registration helpers such as `Add{Target}InfrastructureCore`.
- Per-resource `PublishAs{Target}` APIs that attach customization annotations.
- Pipeline steps that validate target presence and create deployment target resources.
- `DeploymentTargetAnnotation` instances linking compute resources to generated target resources.
- Container registry selection through the shared `AddContainerRegistry` and `WithContainerRegistry` APIs.

## Production readiness checklist

A deployment target is production-ready only when it handles the full target contract, not just a happy-path manifest.

DO:

- Define the ownership boundary up front: does the integration provision target prerequisites, or does it deploy only to existing infrastructure? Validate and document anything the user must create out of band.
- Model target-specific concepts as first-class APIs when users need them regularly: identity/service account, public/private access, network attachment, secret references, scaling, health probes, resource limits, regions/locations, ingress, and cleanup behavior.
- Keep raw manifest/customization callbacks as escape hatches, not as the only way to configure common production settings.
- Produce actionable preflight errors for missing CLIs, auth, selected project/subscription/cluster, enabled services/APIs, registry auth, and required permissions.
- Persist useful deploy outputs in deployment state and summary: resource IDs, service URLs, regions, target project/subscription/cluster, and access mode.
- Make apply/deploy idempotent and update-safe. Re-running deploy should converge the target resource rather than fail because it already exists.
- Decide and document the teardown story. If the target supports destroy/delete, wire it into the deployment lifecycle or clearly state that cleanup is external.
- Treat provider defaults as part of the API contract. If a target is private by default, preserve that default and expose explicit opt-in APIs for public access.

DON'T:

- Don't call a target production-ready when it only handles a single HTTP container with no secrets, identity, networking, service references, or cleanup.
- Don't make users edit generated YAML/JSON for common production features.
- Don't silently rely on ambient cloud context when a project/subscription/cluster can be specified in the AppHost model.
- Don't expose insecure convenience APIs that grant broad public access, owner/contributor permissions, or plaintext secrets.

## Environment resources

DO:

- Name environment methods `Add{Target}Environment`.
- Register infrastructure/pipeline services idempotently.
- In run mode, return `CreateResourceBuilder(environmentResource)` when the environment has no runtime representation.
- In publish mode, add the environment resource to the model.
- Create default dashboard/deployment support only when publishing if it is target infrastructure.
- Reuse framework-level `WithContainerRegistry` instead of adding target-specific exported overloads with the same generated name.

DON'T:

- Don't surface publish-only environment resources in local run/dashboard.
- Don't create target infrastructure when no target is being used.
- Don't duplicate generic deployment APIs such as `WithContainerRegistry`; duplicate exports can collide in polyglot SDKs even when C# overload resolution works.

## PublishAs APIs

DO:

- Use `PublishAs{Target}` for per-resource deployment customization.
- Constrain overloads to the resource shapes that can publish to that target, such as `IComputeResource`, `ProjectResource`, `ContainerResource`, or `ExecutableResource`.
- Return unchanged builder outside publish mode.
- In publish mode, ensure target infrastructure is registered and attach a customization annotation.

DON'T:

- Don't mutate the run resource for publish-only customization.
- Don't attach publish customizations outside publish mode.
- Don't silently accept a `PublishAs{Target}` call when no matching environment can exist.

## Pipeline steps

DO:

- Use a marker singleton so global pipeline steps are registered once.
- Split global validation from per-environment target generation.
- Run validation only in publish mode.
- Emit clear errors when a resource has target-specific customization but no target environment exists.
- Use `requiredBy: WellKnownPipelineSteps.BeforeStart` or another precise ordering point.

DON'T:

- Don't register duplicate pipeline steps when multiple environments are added.
- Don't perform deployment-target validation during run mode.

## Generated artifacts

DO:

- Expose customization callbacks over generated models such as Compose files, Kubernetes resources, Bicep resources, or Container Apps.
- Use placeholders for parameters, images, ports, secrets, and environment variables.
- Keep generated output deterministic.
- Snapshot generated output in tests when the artifact shape matters.
- Derive target protocol fields from endpoint transport metadata. For example, HTTP/2/h2c decisions should use `EndpointAnnotation.Transport`, not the URI scheme.
- Keep publish artifacts and deploy-time artifacts separate when deploy requires resolved values.
- Validate metadata keys and values against the target platform. Labels and annotations are not portable across deployment targets; Kubernetes-style keys such as `app.kubernetes.io/name` may be invalid for other targets.
- Add at least one regression test for values the framework generates automatically, such as a project resource's self-referential `HTTP_PORTS` target-port expression.

DON'T:

- Don't hardcode local host values into generated artifacts.
- Don't write secrets directly into generated Compose/YAML/Bicep output.
- Don't resolve secret parameters into plaintext deploy manifests. Use the target's secret reference mechanism, or make the limitation explicit and fail safely.
- Don't assume one deployment target's concepts map exactly to another target.
- Don't copy Kubernetes labels, annotations, probes, or object metadata into another target without checking that target's schema and validation rules.

## Deployment tooling

DO:

- Wrap target CLIs behind a runner abstraction so tests can verify exact arguments without requiring live credentials.
- Pass CLI arguments as an argument list or process-spec collection where possible. A single pre-quoted command string is harder to test and easier to break across shells/platforms.
- Include the full CLI command path in tests, not just the leaf verb. For example, assert `gcloud run services replace`, not merely `services replace`.
- Capture stderr and include actionable failure output without logging secrets.
- Separate preflight checks from deploy actions. Check tool availability, authenticated account/context, target project/subscription/cluster, enabled target APIs/services, registry push/pull access, and required permissions before mutating resources.
- Validate a new deployment publisher against a real target before treating snapshots as sufficient. A useful smoke test builds and pushes an image, applies the generated artifact with the real CLI/API, reads the deployed endpoint/status back from the target, performs one protocol call, and then cleans up the deployed resource and images.
- Query the target system after deploy and verify the expected resource state, URL, and access mode. Do not infer success only from the AppHost process exit code or local CLI output.

DON'T:

- Don't rely on ambient shell quoting behavior that differs across platforms.
- Don't let deploy steps silently skip missing generated artifacts.
- Don't make the first mutating CLI/API call be where users discover missing authentication, disabled APIs, or missing registry permissions.
- Don't rely only on generated YAML/JSON snapshots or fake CLI tests for target acceptance; real targets often reject otherwise plausible metadata, label keys, protocols, IAM defaults, or resource names.
