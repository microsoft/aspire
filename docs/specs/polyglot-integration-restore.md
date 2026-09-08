# Polyglot integration restore

## Purpose

Polyglot AppHosts restore Aspire hosting integrations through two execution paths:

- Package-only integration closures use the bundled managed NuGet implementation.
- Closures containing project references use an SDK-generated restore project.

Both paths must apply the same Aspire-selected package source policy while preserving NuGet's native configuration behavior for credentials, trusted signers, fallback folders, audit settings, relative paths, and other user-owned settings.

## Configuration boundary

The generated integration restore is rooted under the AppHost-specific Aspire integration cache. NuGet configuration discovery starts from that directory.

This boundary intentionally:

- Includes configuration inherited from directories above the integration cache.
- Excludes configuration beneath a nested AppHost directory.
- Keeps referenced projects responsible for their own directory-scoped configuration.

The generated restore project resolves the Aspire server and integration closure. It does not combine the NuGet configuration of every project in the referenced MSBuild graph.

## Effective source policy

`IntegrationRestoreSourceResolver` resolves channel and source customization before the package-only and SDK paths diverge.

The policy accounts for:

- The requested or identity-selected channel.
- Explicit source overrides.
- Local package hives.
- Staging feed overrides.
- The NuGet service-index override.
- Global-packages-folder isolation required by mutable sources.

The result contains the effective source locations, package patterns, and cache-isolation requirements. Downstream restore paths consume this result directly and do not reconstruct built-in feed URLs.

Relative local sources are resolved against the AppHost directory before they are used from the integration cache.

## Native NuGet settings bridge

`Aspire.Managed` owns the narrow operation that requires `NuGet.Configuration`.

For a requested discovery directory, the operation:

1. Loads the normal NuGet hierarchy with `Settings.LoadDefaultSettings`.
2. Returns configuration paths in highest-to-lowest precedence order.
3. Returns non-secret source descriptors containing the source name, resolved location, and enabled state.

The operation does not return credentials, passwords, client certificates, trusted signers, or serialized configuration sections.

The CLI matches effective Aspire source locations to these descriptors using NuGet-compatible source identity rules. Every matching descriptor supplies a source key used by the policy overlay, preserving NuGet's association between each alias and its ambient authentication or transport settings.

## Aspire policy overlay

When the effective policy includes package-source mappings, the CLI writes a small `NuGet.Config` overlay.

The overlay can contain:

- Definitions for effective sources not present in ambient settings.
- A complete `packageSourceMapping` policy with `<clear />`.
- Mapping entries for both the NuGet source key and the effective source location when they differ.
- A controlled global packages folder.
- A `disabledPackageSources` clear when every ambient alias for an explicitly selected source is disabled. Only one selected alias is mapped after the clear; disabled aliases remain excluded when an enabled alias is available.

Mapping both identities supports the two legitimate consumers:

- The generated root uses the ambient source key where one exists.
- Referenced projects can observe a source-location identity contributed through `RestoreAdditionalProjectSources`.

The overlay never copies arbitrary user settings. Authentication, trusted signers, fallback folders, audit settings, and unknown sections continue to come from NuGet's native hierarchy loading.

## Package-only restore

The package-only path:

1. Resolves native settings from the integration cache root.
2. Creates a temporary policy overlay at highest precedence.
3. Passes the ordered overlay and ambient config paths to `Aspire.Managed`.
4. Adds effective source locations only when NuGet has not already resolved the matching configured source.
5. Injects the resulting `ISettings` into `DependencyGraphSpecRequestProvider`.

Configured source descriptors are reused so source names, credentials, protocol settings, and credential-provider behavior remain NuGet-owned.

The temporary overlay is deleted after the restore invocation.

## SDK restore root

The SDK path writes a persistent policy overlay beside `IntegrationRestore.csproj`. Normal SDK discovery loads this file together with the intended ancestor hierarchy.

The generated root project receives `RestoreAdditionalProjectSources` only for effective sources that are not already represented by ambient settings. An empty property is emitted when all effective sources are ambient so the process-level child-project default does not replace the root's authenticated source identity.

The generated project does not set `RestoreConfigFile`. Using normal discovery preserves NuGet settings that cannot be represented as source arguments or project properties.

## Referenced-project source contribution

The SDK process receives an invocation-scoped `RestoreAdditionalProjectSources` environment value containing the effective Aspire source locations. Any existing environment value is preserved and the Aspire locations are appended.

When the source policy requires an isolated global packages folder, the SDK process also receives that folder through `NUGET_PACKAGES`. The generated root and every referenced project therefore use the same source-specific package cache.

This is a best-effort default:

- Referenced projects can override the environment-derived property in their own project configuration.
- Aspire does not inject custom targets into user projects.
- Aspire does not replace a referenced project's NuGet configuration root.
- Aspire does not pass a global command-line property that the project cannot override.

The generated root overrides its own value as described above, while referenced projects independently evaluate the environment default.

## Credentials

Credentials remain in NuGet-owned mechanisms:

- `packageSourceCredentials`
- Environment-based credentials
- Credential providers
- Client certificates
- Authenticated ambient source descriptors

The Aspire overlay contains no copied credential material.

Credential-bearing source URLs are rejected for SDK project-reference restores because the generated project and persistent overlay must remain non-secret. Package-only diagnostics redact credential-bearing source values.

NuGet-generated restore artifacts are not scrubbed or separately isolated by Aspire. Files such as `project.assets.json`, dependency graph specifications, and `.nupkg.metadata` can retain configured source URLs, including inline URL credentials. Authentication should therefore use NuGet credential mechanisms rather than embedding credentials in source URLs.

## Cache identity

Package-only cache identity includes:

- Package identities and versions.
- Target framework and runtime identifier.
- Effective source locations.
- An exact, normalized source-policy identity for isolated global package caches.
- Ordered configuration paths and file bytes.
- Stable content identity for invocation-scoped policy overlays.
- Values of environment variables referenced by NuGet config files.
- Global and fallback package folder inputs.
- The managed restore implementation identity.

SDK restore fingerprints include:

- Generated project content.
- Ordered configuration paths and file bytes.
- Effective child-source environment input.
- Global and fallback package folder inputs.
- Referenced project files and their directory-scoped imports.

SDK restore skipping is disabled when a config file references an environment variable, a project uses a floating or property-driven package version, or explicit MSBuild imports and property-driven project references require evaluation. Running restore again is preferred when an unchanged closure cannot be proven.

## Expected scenarios

| Scenario | Generated root | Referenced projects |
|---|---|---|
| Default channel | Uses the effective channel policy and ambient hierarchy | Receives the same effective source locations as an environment default |
| Internal proxy override | Uses only the proxy selected by the source policy | Receives the proxy location |
| Ambient authenticated source | Uses the ambient source key and NuGet-owned credentials | Receives the source location as a best-effort default |
| Explicitly selected disabled source | Clears inherited disabled-source state under the complete mapping policy | Receives the selected source location |
| Local package hive | Uses an absolute local source and isolated global packages folder | Receives the absolute local source |
| Nested AppHost config | Excluded by the integration-cache discovery boundary | Remains available to projects whose own hierarchy includes it |
| Referenced project outside the AppHost tree | Uses the generated root hierarchy | Receives the effective source locations without replacing its own config |
