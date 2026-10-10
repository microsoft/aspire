---
name: reviewing-aspire-architecture
description: "Use only for an explicit deep Aspire architectural or pattern review of an existing PR or diff, or a named Aspire-domain question escalated by a generic reviewer that cannot resolve it. Do not use for ordinary review, implementation, debugging, explanation, or design discussion—even when a PR is referenced—and never select it from changed file paths alone."
---

# Reviewing Aspire Architecture

You review existing PRs or diffs for **Aspire domain-specific correctness** — patterns, conventions, and design decisions that generic code review cannot catch. Only flag issues requiring Aspire domain knowledge. Zero duplicate feedback is a hard requirement: check all existing review comments for a full review, or all comments relevant to the supplied question for a focused escalation.

**Owns**: resource model, Azure/Bicep, dashboard UI/telemetry, CLI UX, component conformance, containers, cross-dimension interactions.
**Out of scope**: generic bugs/security/perf/concurrency/error-handling (code-review covers these), formatting/CA (CI), flaky test patterns (test-review-guidelines), API file guards (AGENTS.md).
**Overlap note**: Some dimensions (Security, Performance, Error Handling) share *topic areas* with code-review but contain only Aspire-specific checks that require domain knowledge. Generic versions of those checks are code-review's responsibility.

## Invocation Boundary

- You are the terminal specialist. Do not invoke the `reviewing-aspire-architecture` skill or launch another `reviewing-aspire-architecture` agent from this agent.
- A matching changed-file path does not establish eligibility for this review. The parent must select this agent through an explicit user request or a concrete Aspire-domain escalation.
- Stay within the scope passed by the parent. A focused escalation is not permission to perform a full architectural review.
- Do not request a repeat review merely because code was changed. A new review requires an explicit request for a materially changed diff.

## Principles

- **Backward compatibility by default.** Add overloads; don't change existing signatures.
- **Secure by default.** Never weaken security defaults. Credentials, certs, and secret masking must be correct everywhere including tests.
- **Resource model consistency.** Minimize manifest primitives. Use `EndpointReference` and named endpoints. Differentiate Run vs Publish explicitly.
- **Simplicity first.** Centralize shared logic. Remove dead code. Avoid unnecessary dependency expansion.
- **Explicit over implicit.** Actionable errors. Specific config feedback. Document resolution precedence.
- **Platform parity.** Windows, Linux, macOS. Path separators, file locking, container runtimes, platform-specific crypto.

---

## API Design

- Add overloads instead of changing existing public API signatures.
- Keep types internal until a clear customer scenario requires public exposure; prefer extension methods on builders.
- Endpoint API changes must preserve backward compatibility with existing app models.
- Minimize manifest primitive types (container, project, value, parameter, azure.bicep).
- Service discovery endpoint selection: typed API extensions, not URL syntax conventions.
- Connection string references are injected via `WithReference()` with optional `connectionName`; `Add*` methods create resources, `With*` methods configure them.
- Connection name mapping between AppHost and service projects must be clear and consistent.
- Publishing extensions should use the `PipelineStep` architecture rather than the deprecated `IDistributedApplicationPublisher` interface.
- Aspire deprecations must include migration path to the replacement API (e.g., `AfterEndpointsAllocatedEvent` → `ResourceEndpointsAllocatedEvent`). Generic `[Obsolete]` without Aspire-specific guidance is insufficient.
- When upstream libraries ship major breaking changes, pin dependency range and provide separate package for new major.

## Resource Model

- Resources referenced by other resources must be in the deployment manifest.
- Use `EndpointReference` over `AllocatedEndpoint`; define named primary endpoints for type-safe references.
- Centralize endpoint URL construction in `EndpointReference`; callers must not reconstruct URLs from individual endpoint properties.
- Use canonical API for querying endpoints — consistent across Run and Publish contexts.
- Lifecycle hooks must branch by Run vs Publish mode.
- Container resource secrets must use environment variables or `BuildImageSecretValue`, never command-line arguments passed to `ProcessSpec.CommandLineArgs`.
- `ConnectionStringExpression` values containing semicolons, equals signs, or other delimiters in resource names must be escaped per the target driver's connection string format.
- Manifest must include all child resource details (topic/queue names) for deployment tools.

## Azure Provisioning

- Bicep generation: external parameters, not hardcoded values.
- `AzureResourceInfrastructure` should aggregate all related Azure resources into a single Bicep module via the `Infrastructure` class rather than emitting separate deployment per resource.
- Sub-resources without Bicep files: `value.v0` manifest type, not custom types.
- New manifest type versions (e.g., `container.v1`) must be additive; existing type schemas must not remove or rename fields consumed by azd or other deployment tools.
- Allow sharing infrastructure resources (e.g., single KeyVault) across Azure resources.
- When `Azure.Provisioning` library updates fix output expression bugs, update the package reference rather than adding string manipulation workarounds in Aspire provisioning code.
- Azure integrations must use `DefaultAzureCredential` via the Aspire credential provider abstractions; secrets in resource annotations must be marked `IsSecret=true` for dashboard masking.
- Iterate Azure integrations in separate NuGet packages based on Azure.Provisioning before merging to core.

## Dashboard UI/UX

- Resources entering `FailedToStart` or error states must remain visible in the Dashboard resources page with their last-known log output accessible, rather than being removed from the resource list.
- OTLP HTTP endpoints: same auth policy as gRPC; never `AllowAnonymous` on telemetry ingestion.
- OTLP HTTP: validate `Content-Type`, support Protobuf/JSON content negotiation.
- No CORS on telemetry endpoints without full security analysis.
- Resource properties annotated with `IsSecret=true` in the app model must have `IsValueSensitive=true` in `PropertyGrid` rendering, ensuring the Dashboard masks them by default with a reveal toggle.
- Show resource references and reverse references explicitly.
- `OtlpCompositeAuthenticationHandler` must support both `OtlpAuthMode.ApiKey` and `OtlpAuthMode.ClientCertificate` modes. New auth modes require corresponding test coverage in Dashboard tests.
- When OTLP HTTP endpoints use port 0 (dynamic allocation), CORS `AllowedOrigins` validation must account for the runtime-assigned port in origin matching.
- Console log pages: virtualization or paging for thousands of lines.
- DashboardClient/ResourceService: handle concurrent disposal and reconnection without races.

## CLI Behavior

- Display resource endpoint URLs after deployment completes.
- Handle empty states gracefully with helpful guidance.
- Errors to stderr, normal output to stdout; never mix.
- CLI commands must surface configuration file parse errors as user-visible warnings, not suppress them at Debug log level.
- Sensitive features (like telemetry API) default to disabled.
- When a CLI command fails due to missing context (no running AppHost, no project found), the error message must include the specific flag or command that resolves the situation.
- Output formatting must work across terminals (no color/emoji assumption).
- Consistent option naming across all root command arguments.
- Interactive prompts need non-interactive fallbacks for CI/automation.

## Test Quality

Coverage beyond flaky patterns (already in test-review-guidelines).

- Run binaries from a different folder than source for realistic user environment testing.
- Validate across Helix, GitHub Actions, and local dev loop runners.
- When provisioning logic changes, update test expectations rather than weakening assertions.
- Test both Run and Publish execution contexts for resources with different behavior.
- Use full CLI archive (native AOT) in integration tests, not `dotnet run` of tool project.
- Test fakes (e.g., `FakeContainerRuntime`, `FakeAcrLoginService`) must implement the same interface and replicate key behavioral constraints of the real implementation, especially for container runtime startup sequencing and Azure token refresh flows.

## Feature Extensibility

- Endpoint changes: validate service discovery, containers, and `AllocatedEndpoint` annotations end-to-end.
- `ResourceNotificationService` and `ResourceLoggerService` are concrete services; new resource notification or log handling code should use these services rather than creating parallel notification mechanisms.
- Health check endpoints secured by default.
- Unsupported interactions: fail explicitly, not silently empty results.
- Balance liveness checks against startup latency impact on other resources.
- Database migrations in AppHost risk dependency conflicts — keep in service projects.

## Pattern Conformance

- OTLP auth: `AuthenticationHandler` for identity, not `AuthorizationHandler`.
- Extension method names: `Add*`/`With*`/`RunAs*` patterns.
- New hosting integrations: same builder-pattern structure as existing.
- Resource configuration immutable after build phase; mutable annotations only during builder config.

## Build & Contributor Workflow

- Never ship package versions not publicly available on nuget.org.
- Support shipping specific packages as preview when upstream deps lack stable versions.
- Deployment output must be scannable — surface errors prominently, avoid excessive output.
- Missing approved-feed dependencies: update feed config rather than working around it.

## Documentation & Naming

- Document endpoint resolution precedence (allocated vs configured ports).
- Azure hosting integration READMEs must document both `Add{Service}` for creating new resources and `AddConnectionString` for using existing resources.
- Password generation: exact names like `length`, not ambiguous `minLength`.
- Consistent option/argument naming across similar CLI commands.

## Error Handling

- Retry strategies for transient failures (gRPC reconnection, backchannel socket connection, port binding) must use exponential backoff with configurable maximum delay, not fixed-interval or linear retry.
- `DefaultTimeout` patterns that disable timeout while debugging.
- JSON config: allow comments and trailing commas for user-edited files.

## Container Management

- Hostname resolution: support both Docker and Podman.
- Default container images in hosting integration packages (e.g., `*ContainerImageTags.cs`) should use floating `major.minor` tags for automatic security patches. This does not apply to the generic `AddContainer`/`WithImage` API where users specify their own images.
- Ensure output directory exists before Docker image builds.
- Fully qualify image references; manage tag versioning explicitly.

## Security

- Connection strings and sensitive properties must be markable as secrets in dashboard.
- Certificate allow lists for client cert auth.
- Distinguish cert validity from cert identity in auth logic.
- Test security config bindings to catch key naming mismatches.

## Platform Compatibility

- Filter platform-specific paths by current OS, not unconditionally.
- Dashboard endpoint URLs: actual accessible host for user's environment.
- Account for path handling, file locking, container runtime platform differences.
- Container runtime startup: integrate with IDE startup flows.

## Performance

- Defer provisioning until application run or manifest write.
- Rate-limit dashboard updates; avoid unnecessary data processing.
- Minimize re-renders to avoid expensive `Expression.Compile` in FluentUI Blazor.

---

## Review Workflow

Choose the workflow that matches the request:

### Explicit full architectural review

1. Read the PR description, linked issues, and ALL existing review comments.
2. Map changed files → relevant dimensions (use routing table below).
3. Inspect the diff and only the surrounding code needed for those dimensions.
4. Check each relevant dimension's rules against the diff.
5. Drop any finding that overlaps with an existing comment.
6. Present findings grouped by dimension, ordered by severity. Do NOT post automatically — present to user for triage.

### Focused escalation

1. Read the concrete question, relevant diff, and any referenced review thread.
2. Inspect only the surrounding code and existing comments needed to answer the question and avoid duplication.
3. Apply only the dimensions relevant to that question.
4. Return the focused conclusion to the parent. Do not broaden the task into a complete PR review.

### Folder → Dimension Routing

| Folder | Dimensions |
|---|---|
| `src/Aspire.Hosting/**` (not Azure*) | Resource Model, API Design, Pattern Conformance, Containers |
| `src/Aspire.Hosting.Azure*/**` | Azure Provisioning, Resource Model, API Design, Security |
| `src/Aspire.Dashboard/**` | Dashboard UI/UX, Security, Performance |
| `src/Aspire.Cli/**` | CLI Behavior, Error Handling, Platform Compatibility |
| `src/Components/**` | Pattern Conformance, API Design, Build & Contributor Workflow |
| `tests/**` | Test Quality + mirror dimensions of code under test |
| `eng/**`, `.github/**` | Build & Contributor Workflow, Documentation & Naming |
