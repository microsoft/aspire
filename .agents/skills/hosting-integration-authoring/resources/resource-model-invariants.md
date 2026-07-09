# Resource model invariants

The Aspire app model must stay deterministic, composable, and safe to transform.

## Resource names and physical names

Aspire resource names are globally unique model identifiers. Physical names are service-specific names such as database names, queue names, hub names, or cloud resource names.

DO:

- Validate resource names with `ArgumentException.ThrowIfNullOrEmpty`.
- Use resource-name comparison semantics for dictionaries keyed by resource name. First-party integrations can use `StringComparers.ResourceName`; external packages should use an equivalent case-insensitive comparer when that helper is not available.
- Keep child resource names globally unique.
- Provide separate physical-name parameters like `databaseName`, `queueName`, or `hubName` that default to the Aspire resource name.

DON'T:

- Don't use case-sensitive default string dictionaries for resource-name lookups.
- Don't assume child resource names only need to be unique within a parent.
- Don't conflate an Aspire resource name with a provider's physical name constraints.

## Constructor purity

Resource constructors should capture app-model state only.

DO:

- Store immutable or model-time values.
- Validate constructor arguments.
- Keep endpoint references lazy when they depend on annotations.

DON'T:

- Don't resolve services, environment variables, connection strings, endpoint values, files, or network resources in constructors.
- Don't start processes, create containers, or make cloud calls from constructors.

## Annotations

Annotations are the primary extension mechanism for resource behavior.

DO:

- Use annotations for configuration that pipeline steps, publishers, code generators, or runtime callbacks consume.
- Use `ResourceAnnotationMutationBehavior.Replace` for override/last-wins settings.
- Accumulate annotations only when multiple entries are intentionally meaningful.
- Copy annotations carefully when swapping or wrapping inner resources.
- Document non-obvious annotation ordering or replacement behavior.

DON'T:

- Don't add duplicate mutually-exclusive annotations.
- Don't rely on `TryGetLastAnnotation` without `Replace` when repeated calls should override.
- Don't mutate another resource's annotations unexpectedly.

## Model transformations

Some integrations replace or wrap resources in specific modes, such as Azure resources that run locally as containers.

DO:

- Keep references and connection properties valid after transformation.
- Preserve annotations that should apply to the runtime resource.
- Remove or hide resources that no longer represent a real runtime entity.
- Test both pre-transform authoring shape and post-transform run/publish shape.

DON'T:

- Don't leave dangling active resources that duplicate the same runtime role.
- Don't make transformations depend on user call order unless documented and tested.

## Mutability and thread safety

The model may be inspected by callbacks and pipeline steps.

DO:

- Prefer explicit mutation APIs over exposing mutable collections publicly.
- Use read-only views for resource collections.
- Synchronize test fakes or use concurrent collections when callbacks mutate state concurrently.

DON'T:

- Don't expose broad mutable state as public API without a strong reason.
- Don't rely on resource object reference equality when resource names are the identity.
