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

## Feedback & contributing

https://github.com/microsoft/aspire
