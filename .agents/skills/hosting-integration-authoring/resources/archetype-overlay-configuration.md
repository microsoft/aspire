# Archetype: overlay or configuration-object integration

Use this archetype for integrations that add app-model configuration without directly producing a resource. Orleans-style APIs are the main pattern.

This archetype is rare. Most integrations should create resources or configure existing resource builders.

## Shape

An overlay/configuration object may:

- Be returned from an `Add*` method even though it is not an `IResourceBuilder<T>`.
- Hold integration-specific model state.
- Be applied to workloads with `WithReference`, `AsClient`, or another projection method.
- Configure multiple resources without owning a runtime resource itself.

## DO

- Use this shape only when there is a strong model reason.
- Make the API clearly communicate that it creates configuration, not a runtime resource.
- Provide explicit wiring methods for consumers.
- Keep the object small and purpose-specific.
- Document how it affects run and publish behavior.
- Test that applying the overlay to resources produces expected environment, references, annotations, or generated configuration.

## DON'T

- Don't create fake resources just to satisfy the usual `Add*` return pattern.
- Don't return a plain object from a resource-producing API.
- Don't hide global model mutations behind a name that sounds like a local resource.
- Don't make overlay application order-dependent unless order is the feature.

## Naming guidance

Resource-producing methods should still return `IResourceBuilder<T>`. Only non-resource overlay methods should return a custom model/configuration object.

When reviewing, do not flag a non-resource return type by itself. First determine whether the API is intentionally an overlay and whether the wiring APIs are clear.
