# Production-ready deployment target integrations

Use this resource with `archetype-deployment-target-publisher.md` when a deployment integration is expected to create, update, or delete real infrastructure, not just emit artifacts.

Production readiness is not one feature. It is a set of explicit contracts for ownership, state, prerequisites, security, service references, update behavior, teardown, tests, and docs.

## Aspire concepts to map

Start every deployment target design by mapping each Aspire concept to target-native concepts, an explicit unsupported error, or a documented external prerequisite. Do this before adding APIs.

| Aspire concept | What it means in Aspire | Deployment target mapping questions |
| --- | --- | --- |
| Resource name | Stable graph identity used by references, dashboard, logs, and generated output. | What physical name is created? How is it normalized? Is it unique per environment? Where is the original Aspire name recorded for traceability? |
| Physical name | Provider-visible name that may differ from the Aspire name. | Does the target have length/character constraints? Is the name deterministic? How are collisions handled? |
| Compute resource | `ProjectResource`, `ContainerResource`, `ExecutableResource`, or another `IComputeResource` that becomes a workload. | What target workload primitive is created: service, deployment, job, function, task, container app, pod? Which compute resource kinds are supported or rejected? |
| Compute environment | `IComputeEnvironmentResource` receives compute resources during publish/deploy. | What target context is represented: project, subscription, cluster, namespace, region, environment, account? Is it run-visible or publish/deploy-only? |
| Container image | Built image or existing image selected through container annotations. | Where is the image pushed? How are tags chosen? Is digest pinning supported? Who owns pushed images and cleanup? |
| Container registry | Shared `IContainerRegistry` model and `WithContainerRegistry`. | How does the target authenticate/pull images? Is registry creation external or managed? How are registry credentials checked? |
| Endpoints | Named endpoints with scheme, transport, host port, target port, and reference endpoint semantics. | Which target port is exposed? Is TLS terminated by the platform? Does transport map to protocol fields such as HTTP/2/h2c? Which endpoints are public, private, health-only, or unsupported? |
| Service discovery | `WithReference` injects logical service endpoints into consumers. | Does the target provide stable DNS/service binding before deploy? If URLs are post-deploy outputs, do cross-service refs fail, use native bindings, or require a two-phase update? |
| Connection strings/properties | Structured resource values passed to consumers. | Can the target express them as env vars, service bindings, secrets, config maps, managed identity references, or provider-native dependency bindings? |
| Environment variables | Literal and structured values emitted through environment callbacks. | Which values can be resolved at publish, deploy, or runtime? Which values must stay as target-native references? |
| Parameters | User-provided or generated configuration values. | Are non-secret parameters emitted as literals/placeholders? Are secret parameters rejected, mapped to native secrets, or ingested through a secure managed secret path? |
| Secrets | Secret `ParameterResource` and secret-bearing connection strings. | What is the native secret store/reference shape? Who creates secret versions? Which runtime identity gets access? How is plaintext prevented from entering files, logs, state, and CLI args? |
| Identity | Deployer identity and runtime workload identity are different. | Which identity applies infrastructure? Which identity does the workload run as? How are least-privilege roles assigned or validated? |
| Access mode | Whether endpoints are private, internal, authenticated, or public. | What is the secure default? What API explicitly opts into public access? How are caller identities/IAM represented? |
| Networking | Internal/external endpoints, private egress, VPCs, DNS, ingress. | Does the target need VPC connectors, private endpoints, namespaces, subnets, or firewall rules? Are these existing prerequisites or managed resources? |
| Health checks/probes | Aspire health and readiness influence startup and dashboard. | What target readiness/liveness/startup probes are generated? Which endpoints are health-only and excluded from references? |
| Resource limits/scaling | CPU/memory, replicas, min/max scale, concurrency. | Which target knobs map to Aspire concepts? Which target constraints need validation? What defaults are preserved? |
| Arguments/command | `IResourceWithArgs` and container entrypoint/args. | How are args preserved without shell quoting issues? Does the target distinguish command from args? |
| Files/config | Generated config files, `WithContainerFiles`, manifests. | Are files baked into images, mounted, uploaded, or converted to provider config objects? Are secrets excluded? |
| Relationships/waits | `WithReference`, parent/child, wait annotations, dependencies. | Which relationships affect deployment order, IAM, service binding, or cleanup order? Which are dashboard-only and should not affect target output? |
| Parent/child resources | Databases, queues, topics, deployments, containers, models. | Are child resources target objects, app-level config, or unsupported? Who owns their lifecycle and deletion? |
| Dashboard URLs/commands | User-facing resource links and actions. | Which deployed URLs are summary links? Which are details-only? Are commands safe and target-aware? |
| Deployment state | Persisted outputs from prior deploys. | What IDs, URLs, ownership, image tags/digests, and schema version are stored? How is stale state handled? |
| Destroy/cleanup | Removing deployed resources. | Which target objects are managed and safe to delete? Which are retained? What order and retry behavior is required? |
| Polyglot exports | TypeScript/Python/etc. AppHosts use generated SDKs. | Are APIs callback-free or adapter-backed where needed? Are DTOs/exported models ATS-compatible? Are generated names stable and collision-free? |

DO:

- Include this mapping in design notes, review comments, or implementation plans for new deployment targets.
- For every unsupported mapping, fail with an actionable message and add a test.
- Prefer target-native mechanisms for concepts the target owns, such as service bindings, identity, secrets, probes, and traffic.

DON'T:

- Don't silently drop an Aspire concept because the target lacks a direct equivalent.
- Don't collapse structured Aspire values into strings before deciding which lifecycle phase can resolve them.
- Don't expose target-specific escape hatches as the only way to satisfy common Aspire concepts.

## Maturity levels

Classify the target before designing APIs:

| Level | Contract | Examples |
| --- | --- | --- |
| Artifact publisher | Generates artifacts only. Another tool applies them. | Kubernetes YAML export, Docker Compose files |
| Existing-target deployer | Applies artifacts to an existing target and registry. Preconditions are validated, not provisioned. | Cloud Run deploy to an existing project/repository |
| Provisioning deployer | Creates or updates prerequisite infrastructure and then deploys workloads. | Creating registries, service accounts, APIs, namespaces, secret stores |
| Reconciler/operator | Continuously or repeatedly converges desired state, supports drift, commands, and deletion. | Controller-backed cloud or cluster integration |

DO:

- State the level in XML docs, README, tests, and review notes.
- Keep lower levels honest. If prerequisites are external, validate and document them instead of pretending they are managed.
- Move to a higher level only when the integration owns state, permissions, update, and cleanup semantics for that level.

DON'T:

- Don't auto-create chargeable, policy-governed, or shared infrastructure from an artifact publisher without an explicit provisioning contract.
- Don't call an existing-target deployer production-ready if users must edit generated artifacts for common production settings.

## Ownership and deployment state

Every real target object must have an ownership model.

Use these ownership categories:

- **Managed**: Aspire created it and may update/delete it.
- **Referenced existing**: User supplied it; Aspire may read/validate but must not mutate unless a specific API says so.
- **Generated deployment artifact**: Created for a deploy run, such as image tags or manifests. Cleanup policy must be explicit.
- **Shared prerequisite**: Used by multiple apps/environments. Default to retain.

DO:

- Record stable target identifiers in deployment state: project/subscription/cluster, region, resource ID/name, URL, identity, access mode, and ownership.
- Include a state schema/version so future code can migrate or ignore old entries safely.
- Use target labels/tags/annotations to mark Aspire-managed resources when the platform supports it, but never rely on labels alone for deletion.
- Prefer explicit `AsExisting`, `PublishAsExisting`, `WithExisting*`, or `WithManaged*` APIs when ownership is ambiguous.
- Treat app model names and physical target names separately. Store both when they differ.

DON'T:

- Don't delete or overwrite resources discovered only by matching names.
- Don't change a referenced existing resource's IAM, networking, lifecycle, or data-plane state unless the API name and docs make that mutation explicit.
- Don't store secrets in deployment state.

## Pipeline shape

A production deployer usually needs these phases:

1. Model validation: app-model shape, required environment, unique physical names, unsupported references.
2. Preflight: tool/SDK availability, authenticated account, target context, enabled APIs, registry access, permissions, quota/policy checks where possible.
3. Build/push: image build and registry push using structured registry annotations.
4. Prepare artifacts: emit deploy-time artifacts with late-bound values resolved only when safe.
5. Apply: create/update target resources idempotently.
6. Verify: query provider state and confirm ready URL/status/access mode.
7. Record: persist target outputs and ownership in deployment state.
8. Destroy/cleanup: delete only managed resources according to the ownership model.

DO:

- Keep preflight non-mutating.
- Make apply idempotent and retry-safe. Re-running the same deploy should converge the target.
- Verify against the provider after apply; do not infer success from command exit alone.
- Save outputs only after verification succeeds.
- Use structured process arguments or SDK calls; quote only at display/logging boundaries.

DON'T:

- Don't perform cloud API calls during app-model construction.
- Don't let the first mutating call be where users discover missing permissions, disabled APIs, or invalid context.
- Don't leave partial failures success-shaped. Surface the failed operation, resource, target context, and sanitized provider error.

## Publish assets vs deploy actions

Design `aspire publish` and `aspire deploy` as separate contracts.

`aspire publish` produces assets. Assets are the desired deployment shape and supporting files that a user can inspect, review, archive, or hand to another deployment system.

Common publish assets:

- Target manifests such as YAML, JSON, Bicep, Terraform-like files, Compose files, Helm values, or provider-specific descriptors.
- Generated Dockerfiles, config files, container-file inputs, and build metadata.
- Image references, tag placeholders, build contexts, or container registry requirements.
- Parameter placeholders and target-native secret references.
- A deployment plan or README fragment that lists prerequisites and unsupported mappings.
- Non-secret metadata that explains target context, resource names, and access mode.

`aspire deploy` consumes assets and mutates the target. Deploy may generate a second deploy-time asset set when values are intentionally resolved later, but the difference from publish output must be explicit.

Common deploy actions:

- Validate credentials, selected account/project/subscription/cluster, enabled provider APIs, registry access, and permissions.
- Build and push images, or resolve image digests/tags.
- Resolve non-secret late-bound values that are safe to write.
- Apply target manifests or call provider APIs.
- Create or update IAM/access bindings that the user explicitly opted into.
- Query target state, URLs, revisions, routes, and readiness.
- Persist deployment state and outputs.

DO:

- Keep publish output deterministic and reviewable.
- Use placeholders or target-native references for values only deploy can know, such as image tags, generated resource IDs, service URLs, and secrets.
- If deploy needs resolved manifests, write them as separate deploy-time artifacts so users can compare publish assets with applied assets.
- Test `aspire publish` without live credentials for artifact-only scenarios, and test `aspire deploy` with live/faked provider state for mutation scenarios.
- Document which files are publish assets, which files are deploy-time assets, and which commands apply them.

DON'T:

- Don't make publish output look fully resolved when deploy will replace critical fields later.
- Don't write deploy-only values, provider state, or secret material into publish assets.
- Don't let deploy silently ignore stale publish assets or missing generated files.
- Don't require users to run deploy to learn what assets would be produced.

## Prerequisite provisioning

Provisioning prerequisites is a separate design contract from deploying workloads.

Common prerequisites include:

- Enabled cloud APIs/services.
- Container registries/repositories.
- Service accounts or managed identities.
- IAM role bindings.
- Secret stores and secret versions.
- Networks, private endpoints, VPC connectors, namespaces, or clusters.

DO:

- Decide whether each prerequisite is out-of-band, existing-reference, or managed by Aspire.
- Prefer first-class resource APIs for managed prerequisites instead of hidden side effects inside deploy.
- Make mutating prerequisite creation explicit. Use names like `Add{Target}Registry`, `WithManagedServiceAccount`, or `WithPrerequisiteProvisioning` only when the behavior is clear.
- Validate external prerequisites with actionable errors and exact commands/docs where possible.
- Respect org policies, quotas, billing, and permission failures as first-class errors.

DON'T:

- Don't silently enable cloud APIs, create repositories, or grant IAM from a generic `Add{Target}Environment` call.
- Don't require broad owner/contributor permissions when narrower deploy/runtime roles are enough.
- Don't make prerequisites untestable by hiding them behind ambient CLI state.

## Destroy and cleanup

Destroy is risky because current code may not match the resources created by previous deploys.

DO:

- Base destroy on deployment state plus target verification, not only the current app model.
- Delete in reverse dependency order: traffic/routes before services, services before identities/permissions when needed, images last.
- Default to retaining shared prerequisites and user-provided existing resources.
- Make destructive image cleanup opt-in or policy-based, especially for registries shared across deployments.
- Support dry-run/plan output when the target surface is broad or deletion is expensive.
- Treat missing resources as successful convergence, but report resources that cannot be proven safe to delete.

DON'T:

- Don't delete by prefix/name without an Aspire-managed state entry or verified ownership marker.
- Don't delete secrets, registries, networks, or identities that may be shared unless the user explicitly opted into managed ownership.
- Don't ignore partial cleanup failures; persist enough state for a retry.

## Service-to-service references

Deployment targets differ in when service addresses exist.

Use this decision table:

| Target capability | Publish behavior | Deploy behavior |
| --- | --- | --- |
| Stable logical name exists before deploy | Emit target-native reference, DNS name, or service binding expression. | Verify it resolves or target reports ready. |
| URL exists only after deploy | Fail publish for cross-resource endpoint refs, or use an explicit two-phase deploy/update design. | Record URL in deployment state and expose it as output, not as a publish-time env var. |
| Private service auth required | Emit identity-aware binding only when target supports it. | Configure least-privilege caller identity/IAM and test authenticated calls. |
| Unsupported target translation | Fail with guidance. | Do not silently drop service-discovery variables. |

DO:

- Model stable target-native references as structured values.
- Keep deployed URLs as deploy outputs unless the target offers a stable pre-deploy address.
- Distinguish user-facing URLs from workload-to-workload connection contracts.
- Test multi-service apps before claiming service references are supported.

DON'T:

- Don't serialize local run-mode URLs into publish/deploy output.
- Don't make users scrape dashboard summaries for values another resource needs.
- Don't invent service discovery semantics the target does not provide.

## Secrets and identity

Secret and identity support is required for production deployment.

DO:

- Support existing native secret references first, such as secret name/version/key.
- If managing secrets, create/update them through a secure API/SDK path, never by writing plaintext into generated manifests, logs, command-line args, or deployment state.
- Model runtime identity separately from deployer identity. The deployer applies infrastructure; the runtime service account/managed identity accesses dependencies.
- Wire least-privilege roles for runtime identity when the integration owns both sides of a dependency.
- Preserve private-by-default access unless there is an explicit public-access API with security-focused docs and tests.
- Test both secret reference output and denied plaintext secret materialization.

DON'T:

- Don't resolve `ParameterResource` secrets into deploy-time files unless the file is the target's secure ingestion mechanism and is cleaned up immediately.
- Don't grant broad roles such as owner/contributor/editor for convenience.
- Don't make anonymous ingress/public access the default to simplify smoke tests.

## Target feature modeling

For common production features, prefer first-class APIs over raw manifest editing.

Good first-class candidates:

- Public/private ingress and caller access.
- Runtime identity/service account.
- Secret references.
- Scaling/concurrency/resource limits.
- Health/startup probes.
- Registry/project/region/cluster context.
- Network attachment/private egress.
- Data service bindings such as database or queue connections.
- Custom domains/routes when the target owns routing.

DO:

- Keep escape hatches for rare or fast-moving target features.
- Validate combinations that the target rejects, such as private ingress with public unauthenticated access when incompatible.
- Keep APIs at the Aspire concept level when possible, and translate to target-specific schema internally.

DON'T:

- Don't force users to know the target YAML/JSON schema for common scenarios.
- Don't expose raw provider DTOs as the main API if they project poorly to polyglot AppHosts.

## Stabilization gate

Do not stabilize a deployment target package until all applicable items are true:

- Run mode remains clean: publish/deploy-only resources are hidden or no-op locally.
- Publish produces deterministic artifacts for project and container resources.
- Deploy preflight covers tool, auth/context, target API/service enablement, registry access, and permissions.
- Real smoke deploy builds, pushes, applies, verifies provider state, calls the endpoint, and cleans up.
- Private/default access and explicit public access are both tested when public access is supported.
- Secrets use native secret references or managed secret APIs; plaintext secret materialization is rejected.
- Service-to-service references are either target-native and tested or fail clearly.
- Destroy/cleanup behavior is implemented or documented as external with safe manual commands.
- Polyglot analyzer diagnostics are clean and generated SDK signatures have been inspected.
- README documents prerequisites, deploy command, access mode, secrets, cleanup, limitations, and official target docs.
