# Resource projections: configuration and identity

A resource projection gives an existing resource a typed container view without
adding a second logical resource to the application model. The owner and
projection share their name and annotation collection, but they are different
objects and can implement different interfaces.

The builder returned by a projection API still refers to the owner. The builder
passed to its configuration callback refers to the container projection:

```csharp
var worker = builder.AddExecutable("worker", "worker", ".");

worker.RunAsContainerImage("contoso/worker:1.0", container =>
{
    container.WithEnvironment("MODE", "development");
    container.Resource.Entrypoint = "/app/worker";

    IResource owner = container.Resource.GetOwnerOrSelf();
    Debug.Assert(ReferenceEquals(owner, worker.Resource));
});
```

The callback runs synchronously after the projection is registered. The selected
projection instance is reused by compatible subsequent configuration calls.
Capturing that instance for later container-specific work is supported; treating
it as the exact object in the application model is not.

This distinction also exists in the earlier `RunAsEmulator` pattern, whose
configuration callbacks receive separate container wrappers. Projections make
the distinction explicit and provide a supported way to resolve registered
projections back to their owners.

## Writing identity-sensitive extensions

Use `GetOwnerOrSelf()` when maintaining resource-keyed dictionaries, storing
logical resource references in custom annotations, comparing identities, or
checking model membership. Normalize both the stored key and subsequent lookups.
Casting a projection to `IResource` does not change its identity.

```csharp
public static IResourceBuilder<T> TrackResource<T>(
    this IResourceBuilder<T> builder,
    IDictionary<IResource, string> labels)
    where T : IResource
{
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(labels);

    labels[builder.Resource.GetOwnerOrSelf()] = "tracked";
    return builder;
}
```

`GetOwnerOrSelf()` returns the same instance for ordinary resources, including
owners that have a selected projection. It does not look up resources by name,
unwrap arbitrary wrappers, or infer ownership merely from shared annotations.
In particular, an unregistered custom wrapper does not acquire an owner mapping.

When implementing a custom projection factory or constructor, use the owner
passed to that factory or constructor directly. The custom projection is not yet
registered, so its owner cannot be resolved through `GetOwnerOrSelf()` until
registration completes. It is available inside the configuration callback.

## Keep typed access separate from identity

`GetOwnerOrSelf()` returns `IResource`, not the input's generic type: an executable
owner cannot be returned as a `ContainerResource`. Continue using the projection
for its container properties and extension methods.

Similarly, a typed resource-event callback registered through a projection
receives that projection as its typed resource argument. Use `GetOwnerOrSelf()`
when that callback needs logical identity instead.

An `EndpointReference` uses the owner as its `Resource` when the owner implements
`IResourceWithEndpoints`. Otherwise, it retains the projection that provides the
endpoint contract. Its dependency references resolve to the owner in either case.
For other contracts, prefer the owner when it implements the contract and fall
back to the projection when it does not.

Framework identity boundaries resolve registered projections automatically.
They cannot rewrite references stored by custom code or change the result of
`ReferenceEquals` on the two distinct objects. Existing public resource and
builder interfaces retain their signatures; extension authors opt into explicit
identity resolution where their own state requires it.
