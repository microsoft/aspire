# Pipeline output directories and diffing

`IPipelineOutputService` exposes application-wide and resource-specific output paths, plus temporary workspaces.
Artifact producers and consumers resolve it through host dependency injection. Custom providers can supply an alternate
layout. Opt-in output diffing detects stale generated artifacts by regenerating them from the current source and
comparing the result with existing output.

## Design goals

- Give pipeline entry points and steps a consistent source of output-directory defaults.
- Let artifact producers and consumers resolve the same destinations through DI.
- Support global and resource-specific output directories.
- Provide temporary build storage separately from deployment output.
- Check generated output through the default differ or a custom implementation.

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
| `GetOutputDirectory(resource)` | The global output directory plus `resource.Name`. |
| `GetTempDirectory()` | The temporary workspace allocated through `IFileSystemService`. |
| `GetTempDirectory(resource)` | The temporary workspace plus `resource.Name`. |

Output lookups return paths without creating their deployment-output directories. The default provider allocates its
temporary workspace when constructed; resource temporary-directory lookups return paths beneath that workspace. Callers
create resource subdirectories before writing. `IFileSystemService` manages the temporary workspace's existing cleanup
and preservation behavior.

A custom `IPipelineOutputService` registered in host DI controls the paths returned by each lookup. Publishers and
consumers that resolve the service use those results directly.

## Artifact producers and consumers

Publishers choose artifact filenames and which directory lookup suits their layout. Resources can share a directory.
Each publisher controls its file creation and cleanup; consumers must use the producer's directory when reading those
files.

Docker, Kubernetes, and Radius environment publishers use the global output lookup when the model contains zero or one
`IComputeEnvironmentResource`. With multiple compute environments, including child compute environments and mixed
environment types, they use the publishing environment resource's output lookup.

Azure's root publishing resource is an `AzureEnvironmentResource`, distinct from its compute environment resources. It
uses the global output lookup. Its modules and deployment-target artifacts form one tree with relative references.
Direct publisher contexts that receive a destination from their caller use that destination.

## Output diffing

The output diffing APIs are experimental (`ASPIREPIPELINES005`).

In CI, verify that checked-in deployment files are up to date with the current source. The check regenerates the files
and fails if selected output differs, leaving regenerated files on disk for inspection. Output diffing is opt-in:

```csharp
builder.WithOutputDiffing();
```

Run `aspire do <target>` to execute a comparison target and its dependencies. The default registration names the target
`publish-and-diff` and waits for `publish` to complete.

### Dependencies and capture timing

The baseline captures before every selected step action. This protects checked artifacts written by prerequisites and
build steps as well as publishers. When the comparison target is not selected, the baseline returns early. Running
`aspire do publish` therefore performs generation only; an alias that depends on `publish-and-diff` includes the check.

Step factories and configuration callbacks finish before the baseline and must not write checked artifacts.
Inspection resolves the graph without capturing output.

### Configuring the comparison step

Choose the generation target, comparison name, and description:

```csharp
builder.WithOutputDiffing(
    targetStep: "generate",
    diffStepName: "check-generated-files",
    description: "Check generated deployment files");
```

When `diffStepName` is omitted, it defaults to the target name followed by `-and-diff`. The C# factory overload accepts
a complete `PipelineStep` for dependencies, tags, and an action:

```csharp
builder.WithOutputDiffing(() => new PipelineStep
{
    Name = "check-generated-files",
    DependsOnSteps = ["generate", "postprocess"],
    Tags = ["ci"],
    Action = _ => Task.CompletedTask
});
```

The supplied dependencies and action finish before the second capture and comparison. Postprocessing must finish within
that boundary, for example in the supplied action or a final action on a dependency such as `publish`. Final actions
added to the comparison step after registration and `AfterPublishEvent` run too late for their writes to be checked.

Separate comparison targets can check different generation pipelines. Their baselines do not depend on one another.

### Capture scope and file selection

Each capture calls `IPipelineOutputService.GetOutputDirectory()` once to resolve the global output boundary. The default
differ recursively captures selected entries beneath it, including resource directories created during generation.
Generators should write checked artifacts beneath this directory; diffing does not enforce that convention. Temporary directories and resource destinations outside the global directory need custom coverage.

Ordered glob rules select files relative to the global directory. An empty `Rules` collection implies `Include **/*`.
A nonempty collection replaces that default, starts with no selected files, and applies rules in order; the last matching
rule wins. `Include` selects matches and `Exclude` removes them. A later include can restore matches beneath an excluded
parent. Exclude-only rules select nothing. To exclude source from a broad comparison:

```json
[
  { "Action": "Include", "Pattern": "**/*" },
  { "Action": "Exclude", "Pattern": "src/**" }
]
```

The default differ reads `IOptions<PipelineOutputDiffOptions>`, bound to `Pipeline:OutputDiff`. Configuration-provider
precedence, `Configure`, `PostConfigure`, and options validation apply. For example, check generated files even when Git
ignores them:

```json
{
  "Pipeline": {
    "OutputDiff": {
      "Mode": "FileSystem",
      "Rules": [
        { "Action": "Include", "Pattern": "generated/**" },
        { "Action": "Exclude", "Pattern": "**/*.tmp" }
      ]
    }
  }
}
```

Call `WithOutputDiffing()` before registering `Configure` callbacks so configuration binds first.
`PostConfigure` callbacks run after all configuration callbacks:

```csharp
builder.Services.Configure<PipelineOutputDiffOptions>(options =>
{
    options.Mode = PipelineOutputDiffMode.FileSystem;
    options.Rules.Add(new() { Action = "Include", Pattern = "generated/**" });
});
builder.Services.PostConfigure<PipelineOutputDiffOptions>(options =>
    options.Rules.Add(new() { Action = "Exclude", Pattern = "**/*.tmp" }));
```

Actions are case-insensitive `Include` or `Exclude`. Patterns must be nonempty platform-relative globs without `..`
segments. Invalid options fail capture. Configuration arrays merge by index; review the resulting order when layering
configuration.

Each default snapshotter reads rule values when constructed; Git membership is reevaluated at each capture. Filesystem captures apply these
rules to `.git` entries too; to omit them, add `Exclude **/.git` and `Exclude **/.git/**` after an include.
Missing directories contribute no files. Root links are rejected and filesystem errors propagate. Paths compare
ordinal-ignore-case on Windows and ordinal elsewhere. Captures are not atomic.

### Git and hashes

`PipelineOutputDiffOptions.Mode` controls membership and built-in hashes:

| Mode | Strategy |
| ---- | -------- |
| `Auto` (default) | Use native Git when the executable and a usable worktree are available; otherwise use filesystem membership and streaming XxHash3. |
| `Git` | Require Git and a usable worktree, with a clear failure when discovery cannot find them. |
| `FileSystem` | Use filesystem membership and raw-byte XxHash3, including matching ignored files, with no engine Git invocations. |

In `Auto` and `Git` modes, each capture discovers Git when its output directory exists. Missing directories contribute empty captures.
Errors and cancellation after discovery propagate without switching strategies.

Git membership retains tracked files and untracked, nonignored files among the glob matches. Git's ignore rules include
nested ignore files and repository and global excludes. Tracked files remain eligible even when an ignore pattern matches
them.

Git captures reuse index object hashes for trusted ordinary files with a clean working-tree status. Index-ignore hints
require rehashing; potentially changed and untracked ordinary files use one `hash-object --stdin-paths` batch per capture.
This batch rejects filenames containing CR or LF. Git attributes govern EOL normalization and clean-filter semantics. The comparison uses working-tree captures before and
after generation: a dirty but unchanged artifact passes. Git-reported modes affect comparison only when both captures
report a mode, so staging an unchanged untracked entry does not itself report a change.

Links compare their target text rather than the destination's contents. Each capture reads `FileInfo.LinkTarget` and
hashes its UTF-8 text with `hash-object --stdin`; index hashes are not reused for links. A regular link-storage file under
`core.symlinks=false` is hashed as ordinary content. Default capture excludes entries Git reports as deleted from the
working tree.

Capture makes no Git storage writes. Files regenerated during a failed check remain available.

### Comparison results and customization

Reported changes fail the check with a `DistributedApplicationException`. Paths are relative to the output directory
and sorted by platform path order. Each report line is formatted as `Kind: path`.

Select a custom snapshot type and its capture and comparison implementations to own coverage and comparison policy:

```csharp
builder.WithOutputDiffing<MyOutputSnapshot, MyOutputSnapshotter, MyOutputComparer>();
```

`MyOutputSnapshotter` implements `IPipelineOutputSnapshotter<MyOutputSnapshot>` and `MyOutputComparer` implements
`IPipelineOutputComparer<MyOutputSnapshot>`. The generic constraints require both services to use the same snapshot type;
no public snapshot base class or runtime representation cast is needed.
`CaptureAsync(outputDirectory, ct)` returns a snapshot, and `CompareAsync(before, after, ct)` reports differences between snapshots.
See the [comparer contract](../../src/Aspire.Hosting/Pipelines/IPipelineOutputComparer.cs) for snapshot usage.
Custom changes preserve the relative reporting paths supplied by the comparer.

The extension registers transient defaults for the two closed generic interfaces and preserves existing host DI
registrations. Each capture resolves its snapshotter and looks up the current output directory. Only the baseline snapshot
is retained between steps. A comparer is resolved after the second capture. Both services follow
normal host DI ownership: DI-created services are disposed with the host; pre-instantiated services remain caller-owned.
Default capture configuration and Git are not required by custom implementations.

See the [diffing integration](../../src/Aspire.Hosting/PipelineOutputDiffingExtensions.cs),
[default snapshotter](../../src/Aspire.Hosting/Pipelines/DefaultPipelineOutputSnapshotter.cs),
[default comparer](../../src/Aspire.Hosting/Pipelines/DefaultPipelineOutputComparer.cs),
[artifact integration tests](../../tests/Aspire.Hosting.Tests/Pipelines/PipelineOutputDiffingTests.cs), and
[EF graph and writer tests](../../tests/Aspire.Hosting.EntityFrameworkCore.Tests/EFOutputDiffingTests.cs).

## Implementation map

| Area | Primary type | Responsibility |
| ---- | ------------ | -------------- |
| Directory contract | `IPipelineOutputService` | Expose global and resource-specific output and temporary lookups. |
| Default implementation | `PipelineOutputService` | Resolve output defaults and temporary workspace paths. |
| AppHost configuration | `PipelineOptions.OutputPath` | Select the global output directory. |
| Environment layout | `PublishingContextUtils` | Select the Docker, Kubernetes, and Radius environment destination. |
| Temporary storage | `IFileSystemService` | Allocate temporary storage and apply its cleanup policy. |
| Diff target | `PipelineOutputDiffingExtensions` | Capture before and after the selected target, then compare the typed snapshots. |
| Provider contracts | `IPipelineOutputSnapshotter<TSnapshot>`, `IPipelineOutputComparer<TSnapshot>` | Pair capture and comparison through a shared snapshot type. |
| Default selection | `PipelineOutputDiffOptions` | Configure ordered include/exclude rules and the Git strategy. |
| Default capture | Internal `DefaultPipelineOutputSnapshotter`, `PipelineOutputSnapshotter` | Select a capture strategy for each capture and produce file snapshots. |
| Default comparison | Internal `DefaultPipelineOutputComparer` | Compare captured entry kinds, hashes, and available Git modes without reading live output. |

## Resources

- [Output-directory defaults issue #12412](https://github.com/microsoft/aspire/issues/12412): host-side defaults for publishing, deployment, and direct-step entry points.
- [Original output-service PR #12563](https://github.com/microsoft/aspire/pull/12563): resource-specific output and temporary lookups, and service resolution through DI.
- [Predecessor PR #12414](https://github.com/microsoft/aspire/pull/12414).
- [Output service](../../src/Aspire.Hosting/Pipelines/PipelineOutputService.cs) and [environment layout helper](../../src/Shared/PublishingContextUtils.cs).
