# Archetype: Azure provisioning and dual-mode integration

Use this archetype for Azure resources that Aspire provisions, references, or can run locally through an emulator/container.

Representative examples:

- `src/Aspire.Hosting.Azure.Storage/AzureStorageExtensions.cs`
- `src/Aspire.Hosting.Azure.CosmosDB/AzureCosmosDBExtensions.cs`
- `src/Aspire.Hosting.Azure.Sql/AzureSqlExtensions.cs`
- `src/Aspire.Hosting.Azure.Sql/AzureSqlServerResource.cs`

## Provisioning resource shape

Azure-managed resources usually derive from `AzureProvisioningResource` and implement `IResourceWithConnectionString` or Azure-specific target interfaces as needed.

Top-level add methods should call `builder.AddAzureProvisioning()` and create a resource with an `Action<AzureResourceInfrastructure>` callback.

## DO

- Name top-level methods `AddAzure{Service}`.
- Use `AzureResourceInfrastructure` to create Bicep resources, outputs, role assignments, and dependencies.
- Use managed identity and RBAC by default when supported.
- Add provisioning outputs for values used by connection expressions or other resources.
- Add outputs from concrete provisionable-resource properties, not from the Aspire resource's own output references.
- Use secure defaults, for example modern TLS and disabled shared keys when supported.
- Add default role assignments for resources referenced by workloads, especially when the connection string or connection properties advertise managed-identity authentication. The authentication story is incomplete if the workload receives an Azure-auth connection string but provisioning emits no least-privilege data-plane RBAC.
- Support private endpoints and network restrictions when the service requires them.
- Keep child resource provisioning under the parent Azure provisioning resource.

## DON'T

- Don't put child provisioning callbacks on every child unless users truly need child-specific infrastructure customization.
- Don't duplicate emulator/access-key/managed-identity branching in child resources.
- Don't mutate resources marked as existing in ways that imply Aspire owns them.
- Don't hardcode generated Azure names when a naming convention/resource token is required.

## Child resources

Child Azure resources should:

- Implement `IResourceWithParent<TParent>`.
- Have parent-scoped builder methods like `AddDatabase`, `AddQueue`, `AddHub`, or `AddBlobContainer`.
- Use globally unique Aspire resource names.
- Use separate physical-name parameters such as `databaseName`.
- Inherit parent connection properties with `CombineProperties`.

## Existing resources

Existing-resource semantics apply broadly to Azure resources.

| API | Scope |
| --- | --- |
| `RunAsExisting` | Running locally or with development provisioning/reference behavior |
| `PublishAsExisting` | Publishing/deploying infrastructure |
| `AsExisting` | Both run and publish |

The existing-resource annotation records intent that Aspire should reference an existing resource instead of managing it as new. Treat that annotation as read-only deployment intent. Do not apply creation-only mutations such as new auth setup, new child infrastructure, new storage, or new dashboard components to existing resources unless the API explicitly supports that safe operation.

Use `AzureProvisioningResource.CreateExistingOrNewProvisionableResource` when the Azure provisioning package exposes both normal and `FromExisting` constructors for the concrete resource. In `AddAsExistingResource`, check for an already-added provisionable resource with the same Bicep identifier, create `FromExisting(...)` when missing, and call `TryApplyExistingResourceAnnotation(...)`; if that returns `false`, set the provisionable name from a real name parameter/output fallback.

Use polyglot-friendly overloads for string or `ParameterResource` names when exporting these APIs.

## Dual-mode local emulator/container

Azure resources may support local run behavior with `.RunAsEmulator()` or `.RunAsContainer()`.

DO:

- Return unchanged builder in publish mode for run-only emulator/container setup.
- Mark emulators with an emulator annotation.
- Use `InnerResource`, `IsContainer`, or `IsEmulator` to branch resource behavior.
- Delegate annotations to the inner resource when the local container is the runtime resource.
- Make connection properties mode-agnostic, including authentication semantics. Do not emit managed-identity or Azure-specific auth tokens in emulator/container connection strings unless the emulator actually supports that auth mode.
- Copy existing annotations to the inner resource before swapping when needed.
- Remove or hide Azure resources from the run model if the inner container replaces them.

DON'T:

- Don't let emulator endpoints/images leak into publish output.
- Don't leave both Azure and inner container resources active in run mode unless both intentionally run.
- Don't make child resources independently decide whether the parent is Azure or local; centralize mode state in the parent.

## Provisioning callbacks

Provisioning callbacks run during publish/deploy generation. They should produce deterministic infrastructure and use parameters/outputs/references rather than local runtime values.

Do not read allocated ports, local process state, or container runtime state from provisioning callbacks.

Do not create self-referential outputs, such as setting a provisionable resource name from the same Aspire resource's `NameOutputReference` before that output is produced.
