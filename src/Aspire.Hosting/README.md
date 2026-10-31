# Aspire.Hosting library

Core abstractions for the Aspire application model. It provides the building blocks for the distributed application
hosting model. This package should not be referenced by AppHost projects directly. Instead use the `Aspire.Hosting.AppHost`
package for AppHost APIs and targets. Add `Aspire.Hosting.Dotnet` separately to declare .NET
services by their actual project paths with `AddDotnetProject`.

Developers wishing to build their own custom resource types and supporting APIs for Aspire should reference
this package directly.

## Aspire Application Model Overview

Aspire models distributed applications as a graph of **resources**—services, infrastructure elements, and supporting components—using strongly-typed, extensible abstractions. Resources are inert data objects that describe capabilities, configuration, and relationships. Developers compose applications using fluent extension methods (like `AddDotnetProject`, `AddPostgres`, etc.), wire dependencies explicitly, and attach metadata through annotations to drive orchestration, configuration, and deployment.

Legacy .NET-hosting `AddProject` overloads are obsolete but remain functional. See the
[migration guide](../../docs/migrate-from-addproject.md) for overload mappings, returned
resources, generic helpers, model queries, and compatibility boundaries.

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

## Experimental test output collection

Framework integrations can register collectors with `WithTesting(options =>
options.CollectResults(...))`. During `aspire test`, `ResourceTestingContext.CaptureOutputAsync()`
waits for authoritative native executable completion, then replays DCP's separate raw
stdout/stderr through EOF into `stdout.log` and `stderr.log` under `context.OutputPath`.
It writes `output.complete` only after both files close. Repeated/concurrent calls share
one capture, including failures. The helper does not scrape dashboard logs, merge streams,
add timestamps, or impose the dashboard backlog limit.

```csharp
#pragma warning disable ASPIRETESTING001

resource.WithTesting(options => options.CollectResults(async context =>
{
    await context.CaptureOutputAsync();
    // Translate stdout.log into framework-neutral TestResult publications.
}));
```

Only one native, non-terminal executable invocation is supported. Framework integrations
must prevent replicas, restarts, and retries before launch. Failed execution still permits
capture; cancellation and stream/file errors fail collection. Partial files are retained
without the completion marker. Artifacts are exclusively created with Unix 0600 permissions
inside the run's private directory; they cannot overwrite existing files. Ordinary runs
do not activate collectors or create test output paths.

## Feedback & contributing

https://github.com/microsoft/aspire
