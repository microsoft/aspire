---
applyTo: "src/Aspire.Hosting.Azure*/**/*.cs"
---

# Hosting Azure Review Patterns

- Child Azure resource types should implement `IResourceWithParent<TParent>`. Expose parent-scoped domain methods like `AddDatabase`/`AddHub` on the parent builder instead of top-level creation APIs.
- Keep emulator/access-key/managed-identity branching centralized in the parent/base resource. Child resources may append child-specific segments but should not re-implement auth-mode branching.
- For override-style annotations where repeated calls should replace earlier values, use `ResourceAnnotationMutationBehavior.Replace`. Only rely on `TryGetLastAnnotation` without `Replace` when accumulating multiple annotations is intentional.
- Child resource names must be globally unique in the Aspire model; provide a separate physical-name parameter (e.g., `databaseName`) that defaults to the resource name.
- When a resource is marked existing via `RunAsExisting`/`PublishAsExisting`, treat it as read-only. Do not apply auth or provisioning mutations that only make sense for newly created resources.
- Customize Azure provisioning/Bicep through the `AzureProvisioningResource` parent. Child resources are usually plain `Resource` types — expose typed child properties instead of separate provisioning callback APIs.
- Keep health checks side-effect-free. Perform resource initialization (creating databases, containers, queues) in `OnResourceReady` after the resource becomes healthy.
