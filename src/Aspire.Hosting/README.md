# Aspire.Hosting library

Core abstractions for the Aspire application model. It provides the building blocks for the distributed application
hosting model. This package should not be referenced by AppHost projects directly. Instead use the `Aspire.Hosting.AppHost`
package to add a transitive referencing including custom build targets to support code generation of metadata
types for referenced .NET projects.

Developers wishing to build their own custom resource types and supporting APIs for Aspire should reference
this package directly.

## Aspire Application Model Overview

Aspire models distributed applications as a graph of **resources**—services, infrastructure elements, and supporting components—using strongly-typed, extensible abstractions. Resources are inert data objects that describe capabilities, configuration, and relationships. Developers compose applications using fluent extension methods (like `AddProject`, `AddPostgres`, etc.), wire dependencies explicitly, and attach metadata through annotations to drive orchestration, configuration, and deployment.

Key concepts include:

- **Resources:** The fundamental unit representing a service or component in the app model.
- **Annotations:** Extensible metadata attached to resources to express capabilities and configuration.
- **Fluent extension methods:** APIs like `AddX`, `WithReference`, and `WithEnvironment` that guide correct resource composition and wiring.
- **Resource graph:** An explicit, developer-authored Directed Acyclic Graph (DAG) that models dependencies and value flows.
- **Deferral and structured values:** Configuration and connectivity are expressed using structured references, allowing for deferred evaluation and environment-specific resolution at publish and run time.
- **Standard interfaces:** Optional interfaces enable polymorphic behaviors, such as environment wiring and endpoint exposure, for both built-in and custom resources.
- **Lifecycle events and resource states:** The Aspire runtime orchestrates resource startup, readiness, health, and shutdown in a predictable, observable way.

Aspire's approach ensures flexibility, strong tooling support, and clear separation between modeling, orchestration, and execution of distributed .NET applications.

For the full details and specification, see the [App Model document](https://github.com/microsoft/aspire/blob/main/docs/specs/appmodel.md).

## Publisher output paths

Custom pipeline publishers must declare each persistent named output on the owning `PipelineStep` before the pipeline runs. Default paths are relative to the AppHost project directory, so publishers do not need to search for a repository root:

```csharp
var inventoryDefinition = new PipelineOutputDefinition(
    "inventory",
    ".configgen",
    PipelineOutputKind.Directory);

var step = new PipelineStep
{
    Name = "generate-inventory",
    RequiredBySteps = [WellKnownPipelineSteps.Publish],
    Outputs = [inventoryDefinition],
    SupportsOutputPathRelocation = true,
    Action = context =>
    {
        var inventory = context.Outputs.Resolve(inventoryDefinition);
        Directory.CreateDirectory(inventory.OutputPath);

        // Write only through OutputPath. Use LogicalTargetPath when generated
        // content needs to refer to its checked-in destination.
        File.WriteAllText(
            Path.Combine(inventory.OutputPath, "inventory.txt"),
            inventory.LogicalTargetPath);

        return Task.CompletedTask;
    }
};

builder.Pipeline.AddStep(step);
```

`RequiredBySteps = [WellKnownPipelineSteps.Publish]` includes the custom publisher when `aspire publish` runs.

`context.Outputs.AppHostDirectory` is the authoritative AppHost project directory. `OutputPath` is the only path a publisher should write to, while `LogicalTargetPath` identifies the normal checked-in destination and can differ when output relocation is active. Configuration can override a named target with `Pipeline:Outputs:<step-name>:<output-name>:Path`. The existing primary `--output-path` destination is available through `context.Outputs.PrimaryOutput` and should not be redeclared as a named output.

Set `SupportsOutputPathRelocation` to `true` only when the step has no other persistent writes. Named destinations must not overlap each other, and verification-compatible destinations should remain inside the AppHost Git repository.

## Feedback & contributing

https://github.com/microsoft/aspire
