# Pipeline output directories

`IPipelineOutputService` gives artifact producers and consumers a shared directory lookup through host dependency
injection. Publishing, deployment, and individual pipeline steps use the same destinations. AppHost authors can select
resource-specific directories, and custom providers can supply an alternate layout.

## Design goals

- Give pipeline entry points and steps a consistent source of output-directory defaults.
- Let artifact producers and consumers resolve the same destinations through DI.
- Support global and resource-specific output directories.
- Let AppHost authors override resource destinations using the default provider.
- Manage temporary build storage separately from deployment output.

## Directory resolution

A pipeline step resolves the output service through its context:

```csharp
var outputService = context.Services.GetRequiredService<IPipelineOutputService>();
```

The default implementation, `PipelineOutputService`, uses these rules:

| Lookup | Resolution |
| ------ | ---------- |
| `GetOutputDirectory()` with `PipelineOptions.OutputPath` set | The configured path, normalized to an absolute path. An explicit relative path is relative to the current working directory when the service is constructed. |
| `GetOutputDirectory()` without an explicit path | `aspire-output` beneath the configured AppHost directory. The current working directory supplies the base if `AppHost:Directory` is absent or blank, or if normalizing its value with `Path.GetFullPath` throws `ArgumentException` or `PathTooLongException`. |
| `GetOutputDirectory(resource)` without an override | The global output directory plus `resource.Name`. |
| `GetOutputDirectory(resource)` with an override | The resource path described in Resource directory overrides. |
| `GetTempDirectory()` | The temporary workspace allocated through `IFileSystemService`. |
| `GetTempDirectory(resource)` | The temporary workspace plus `resource.Name`. |

Output lookups return paths without creating their deployment-output directories. The default provider allocates its
temporary workspace when constructed; resource temporary-directory lookups return paths beneath that workspace. Callers
create resource subdirectories before writing. `IFileSystemService` manages the temporary workspace's existing cleanup
and preservation behavior.

A custom `IPipelineOutputService` registered in host DI controls the paths returned by each lookup. Publishers and
consumers use those results directly.

## Resource directory overrides

An AppHost can override the default provider's resource destination path:

```csharp
resourceBuilder.WithPipelineOutputDirectory("deployment/api");
```

The extension stores an immutable `PipelineOutputDirectoryAnnotation`. A later call replaces the resource's previous
override.

| Override | Default-provider result |
| -------- | ----------------------- |
| No annotation | `<global-output>/<resource-name>` |
| `deployment/api` | `<global-output>/deployment/api` |
| `.` | The global output directory itself. |
| `../shared-output` | A sibling of the global output directory, after path normalization. |
| A fully qualified directory path | That directory, independent of the global output directory. |

Relative overrides resolve against the global output directory. Validation uses the host platform's path syntax and
permits `.` and `..` segments. Windows drive-relative paths such as `C:output` and root-relative paths such as `\output`
are rejected; fully qualified drive and UNC paths are accepted. A file-looking name such as `deployment.yaml` still
selects a directory, not an exact file.

The constructor checks that the value is a plausible relative or fully qualified path on the host platform. The provider
resolves the final location against the actual global directory. Validation does not check existence, permissions, or
whether another resource uses that directory.

`PipelineOutputDirectoryAnnotation` affects the default provider's `GetOutputDirectory(resource)` lookup. It does not
change the global or temporary-directory lookups. Custom providers determine if or how they interpret the annotation.

## Artifact producers and consumers

Publishers choose artifact filenames and which directory lookup suits their layout. Resources can share a directory.
Each publisher controls its file creation and cleanup, and consumers resolve the same destination when reading those
files.

Docker, Kubernetes, and Radius environment publishers use the global output lookup when the model contains zero or one
`IComputeEnvironmentResource`. With multiple compute environments, including child compute environments and mixed
environment types, they use the publishing environment resource's output lookup. An explicit annotation on that publishing
resource selects resource resolution regardless of the count.

Azure's root publishing resource is an `AzureEnvironmentResource`, distinct from its compute environment resources. It
uses the global output lookup unless it has an explicit annotation, which selects its resource-specific lookup.

Direct publisher contexts that receive a destination from their caller use that destination.

## Implementation map

| Area | Primary type | Responsibility |
| ---- | ------------ | -------------- |
| Directory contract | `IPipelineOutputService` | Expose global and resource-specific output and temporary lookups. |
| Default implementation | `PipelineOutputService` | Resolve global defaults, resource overrides, and temporary workspace paths. |
| AppHost configuration | `PipelineOutputDirectoryAnnotation`, `PipelineOutputResourceBuilderExtensions` | Store and replace an immutable resource directory override. |
| Environment layout | `PublishingContextUtils` | Select the Docker, Kubernetes, and Radius environment destination. |
| Temporary storage | `IFileSystemService` | Allocate temporary storage and apply its cleanup policy. |

## Resources

- [Output-directory defaults issue #12412](https://github.com/microsoft/aspire/issues/12412): host-side defaults for publishing, deployment, and direct-step entry points.
- [Original output-service PR #12563](https://github.com/microsoft/aspire/pull/12563): resource-specific output and temporary lookups, and service resolution through DI.
- [Predecessor PR #12414](https://github.com/microsoft/aspire/pull/12414).
- [Output service](../../src/Aspire.Hosting/Pipelines/PipelineOutputService.cs), [environment layout helper](../../src/Shared/PublishingContextUtils.cs), and [directory routing tests](../../tests/Aspire.Hosting.Tests/Pipelines/PipelineOutputServiceTests.cs).
