# API naming and shape

Aspire hosting integrations should feel consistent across C# and polyglot AppHosts. Public APIs should be predictable, versionable, and hard to misuse.

## Method naming

| Pattern | Use for | Examples |
| --- | --- | --- |
| `Add{Technology}` | Create a top-level resource or integration object | `AddPostgres`, `AddAzureStorage`, `AddGoApp` |
| `Add{Child}` | Create parent-scoped subresources | `AddDatabase`, `AddQueue`, `AddBlobContainer`, `AddModel` |
| `With{Configuration}` | Configure an existing resource builder | `WithDataVolume`, `WithHostPort`, `WithEnvironment` |
| `RunAs{Mode}` | Change local run behavior only | `RunAsEmulator`, `RunAsContainer`, `RunAsExisting` |
| `PublishAs{Target}` | Change publish/deploy behavior only | `PublishAsDockerComposeService`, `PublishAsAzureContainerApp` |
| `AsExisting` | Apply existing-resource semantics in both run and publish modes | `AsExisting` |
| `Configure{Model}` | Mutate a generated deployment or infrastructure model | `ConfigureInfrastructure`, `ConfigureComposeFile` |

## Type naming

Use nouns or noun phrases for public types.

- Primary resources: `{Technology}Resource` or `{Technology}ServerResource`.
- Child resources: `{Technology}{Child}Resource`, for example `PostgresDatabaseResource`.
- Admin/dev companion containers: `{Tool}ContainerResource`, for example `PgAdminContainerResource`.
- Annotations: `{Purpose}Annotation`, not verb-prefixed names.
- Options objects: `{Feature}Options`.

Prefer consistent property names:

- URI-producing values should be named `UriExpression`.
- Connection string expressions should be named `ConnectionStringExpression`.
- Endpoint references should be named after role: `PrimaryEndpoint`, `InternalEndpoint`, `HttpEndpoint`.
- `IServiceProvider` properties should be named `Services`.
- Port customizers should use existing names like `WithHostPort` or role-specific names like `WithGatewayPort`.

## Return types

Resource-producing APIs should return `IResourceBuilder<TResource>`.

Rare overlay/configuration APIs may return a non-resource object only when the integration intentionally does not create a resource. See `archetype-overlay-configuration.md`.

Fluent configuration APIs should return the same builder type they receive, unless they create and return a child resource.

## Parameters

DO:

- Use `[ResourceName] string name` for Aspire resource names.
- Validate `builder` with `ArgumentNullException.ThrowIfNull`.
- Validate required strings with `ArgumentException.ThrowIfNullOrEmpty`.
- Separate Aspire resource names from physical names. Use `databaseName`, `queueName`, or similar optional physical-name parameters that default to `name`.
- Use `IResourceBuilder<ParameterResource>` for secrets and user-supplied credentials.
- Prefer options objects when an API needs many optional parameters.

DON'T:

- Don't make logically required parameters optional just to avoid updating call sites.
- Don't add many optional parameters to public APIs; they are hard to version.
- Don't use `Tuple<>` in public APIs.
- Don't expose implementation-detail annotations, helpers, or generated deployment node types publicly unless users must customize them.
- Don't use boolean parameters when an enum is clearer and likely to grow.

## Annotations

Use `ResourceAnnotationMutationBehavior.Replace` for last-wins configuration, such as build flags, selected package manager, chosen publish mode, or existing-resource settings.

Do not accumulate mutually exclusive annotations unless multiple annotations are intentionally meaningful.

## Experimental APIs

Mark unstable or emerging APIs with `[Experimental("ASPIRE...")]` and use a unique diagnostic ID. Deployment, publishing, compute, language-runtime, and generated-Dockerfile APIs often require experimental treatment.

Do not add obsolete shims for APIs that have not shipped stable unless there is a specific compatibility reason.
