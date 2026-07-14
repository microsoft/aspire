# Selector matrix

Classify an integration by composing values from each axis. The result determines which archetype and cross-cutting resources to apply.

## Axis 1: resource shape

| Shape | Typical base/type | Examples | Primary resource |
| --- | --- | --- | --- |
| Container-backed service | `ContainerResource` | MongoDB, PostgreSQL, Redis, Kafka, RabbitMQ | `archetype-container-backed-service.md` |
| Admin/tool container | `ContainerResource` | Adminer, DbGate, MCP Inspector, k6 | `archetype-admin-and-tool-container.md` |
| Setup or migration helper | `ExecutableResource`, `ContainerResource`, or metadata `Resource` | Flyway, SQL DACPAC deployment, package installers, model pulls | `archetype-setup-and-migration-helper.md` |
| Controller/reconciler | Singleton controller service plus environment/control resource | Azure run-mode provisioning controller, deployment reconciler, local infrastructure operator | `archetype-controller-reconciler.md` |
| Sidecar/middleware infrastructure | sidecar/component resources plus annotations | Dapr sidecars/components, OpenTelemetry Collector | `archetype-sidecar-and-middleware.md` |
| Tunnel/webhook bridge | `ContainerResource` or `ExecutableResource` | Ngrok, Stripe CLI webhook forwarding | `archetype-tunnel-and-webhook-bridge.md` |
| Secret provider/broker | `Resource` plus value providers or pipeline steps | Bitwarden Secrets Manager, external secret brokers | `archetype-secret-provider.md` |
| Azure provisioning service | `AzureProvisioningResource` | Azure Storage, Cosmos DB, Key Vault, Service Bus, Azure SQL | `archetype-azure-provisioning.md` |
| External/cloud reference | `Resource` plus connection properties | OpenAI, GitHub Models, external APIs | `archetype-external-cloud-reference.md` |
| Executable/language app | `ExecutableResource` | Python, Go, JavaScript, Node, Vite, Next.js | `archetype-language-executable-app.md` |
| Deployment target/publisher | environment resource plus deployment target annotations | Docker Compose, Kubernetes, Azure Container Apps | `archetype-deployment-target-publisher.md` |
| Overlay/configuration object | custom non-resource model object | Orleans-style app model overlays | `archetype-overlay-configuration.md` |

## Axis 2: lifecycle mode

| Mode | Meaning | Common API shape |
| --- | --- | --- |
| Run-capable publish/deploy participant | Runs locally and also participates in manifest or deployment output | `Add{Service}`, child `Add{Resource}` methods |
| Run-only | Exists only for local development/run orchestration and is excluded or no-op in publish/deploy output | `With{DevTool}`, setup sibling resources |
| Publish/deploy-only | Exists only while generating or applying deployment output | `Add{Target}Environment`, `PublishAs{Target}` |
| Dual-mode | Has one shape locally and another shape when publishing/deploying | Azure resource with `RunAsEmulator`, `RunAsContainer`, `InnerResource`, `IsContainer` |
| Mode-agnostic reference | Same app model representation in run and publish | External endpoint/API key resources |

## Axis 3: role

| Role | What it does |
| --- | --- |
| Service dependency | Resource referenced by workloads with `WithReference` |
| Workload/app | Runs application code |
| Deployment target | Receives compute resources during publish/deploy |
| Admin/dev companion | Optional local UI/tool container for a parent service |
| Standalone dev tool | Shared or independent local tool not owned by one parent |
| Child/subresource | Database, queue, topic, hub, model, deployment, container, or similar child |
| Setup sibling | Run-mode helper that prepares a workload before it starts |
| Sidecar/middleware | Process or configuration attached to one or more workloads through annotations |
| Bridge/proxy | Local resource that exposes or forwards endpoints to an external system |
| Secret provider | Resource that manages or resolves secrets/credentials for other resources |
| Controller/reconciler | Serializes commands, lifecycle events, background probes, and live-state reconciliation |

## Axis 4: structure

| Structure | Pattern |
| --- | --- |
| Standalone | Top-level `Add{Technology}` returns `IResourceBuilder<T>` |
| Parent-child | Child `Add*` method is exposed on parent builder and child implements `IResourceWithParent<TParent>` |
| Companion | Parent `With{Tool}` method adds a hidden/excluded companion and returns the original parent builder |
| Singleton utility | Top-level `Add{Tool}` returns an existing singleton builder on repeated calls |
| Synthetic facade | Resource has no independent DCP process/container; another resource or external service drives its status, endpoints, and lifecycle |
| Controller-driven | Resource commands and lifecycle hooks enqueue typed intents into a serialized controller/reconciler |
| Annotation-driven | Fluent APIs add annotations; lifecycle hooks discover annotations and materialize derived resources |
| Overlay | `Add*` returns a non-resource configuration object and resource wiring happens through `WithReference`, `AsClient`, or similar |

## Classification examples

| Integration | Classification |
| --- | --- |
| PostgreSQL | Container-backed service + run-capable publish/deploy participant + service dependency + parent-child + admin companion |
| Azure Storage with Azurite | Azure provisioning service + dual-mode + service dependency + child resources + emulator |
| Docker Compose | Deployment target/publisher + publish/deploy-only + environment resource + per-resource `PublishAs*` customizations |
| Python Uvicorn app | Executable/language app + run and publish + workload + generated Dockerfile |
| OpenAI | External/cloud reference + mode-agnostic + service dependency + optional child model/deployment resources |
| Orleans | Overlay/configuration object + resource references to compute resources |
| Adminer | Admin/tool container + run-only + standalone singleton utility |
| Dapr | Sidecar/middleware infrastructure + run/deploy bridging + annotation-driven sidecars/components |
| Ngrok | Tunnel/webhook bridge + run-only + bridge/proxy |
| Dev Tunnel port | Tunnel/webhook bridge + run-only + bridge/proxy + synthetic facade endpoint |
| Azure run-mode provisioning | Controller/reconciler + run-mode visible environment control resource + hidden/excluded publish marker + command-driven resource operations + drift detection |
| SQL DACPAC | Setup/migration helper + run-only or deploy-stage setup + service-state deployment |
