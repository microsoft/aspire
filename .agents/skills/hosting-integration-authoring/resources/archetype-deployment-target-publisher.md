# Archetype: deployment target or publisher integration

Use this archetype for integrations that generate or customize deployment artifacts: Docker Compose, Kubernetes, Azure Container Apps, or similar targets.

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

## Environment resources

DO:

- Name environment methods `Add{Target}Environment`.
- Register infrastructure/pipeline services idempotently.
- In run mode, return `CreateResourceBuilder(environmentResource)` when the environment has no runtime representation.
- In publish mode, add the environment resource to the model.
- Create default dashboard/deployment support only when publishing if it is target infrastructure.

DON'T:

- Don't surface publish-only environment resources in local run/dashboard.
- Don't create target infrastructure when no target is being used.

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

DON'T:

- Don't hardcode local host values into generated artifacts.
- Don't write secrets directly into generated Compose/YAML/Bicep output.
- Don't assume one deployment target's concepts map exactly to another target.
