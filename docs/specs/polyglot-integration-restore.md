# Polyglot integration restore

## Purpose

A polyglot AppHost can reference Aspire hosting integrations as NuGet packages or as .NET projects. These inputs require different restore behavior.

Direct package references need a NuGet restore that works without the .NET SDK and produces a manifest of package assets for the generated AppHost server. Project references need the .NET SDK to evaluate MSBuild imports, conditions, central package management, transitive project references, and copied-local output.

Treating both inputs as one restore closure couples unrelated concerns: adding a project reference can change the restore owner, source policy, cache behavior, and assembly layout used for a direct package. It can also duplicate resolution of direct integration packages across the package and SDK paths. The restore model therefore assigns each input to one owner:

| Integration input | Restore owner | Output |
|---|---|---|
| Direct package reference | Bundled `Aspire.Managed` NuGet implementation | Package probe manifest containing the resolved managed and native package assets |
| Project reference | Generated `IntegrationRestore.csproj` built by the .NET SDK | Immutable copied-local library layout containing project output and its managed, resource, and native dependencies |

A mixed AppHost runs both paths. The generated AppHost server consumes both outputs, but only the server requires special assembly probing. The SDK path represents referenced projects and their dependencies through the standard copied-local layout.

In this document:

- **Generated root** means the generated `IntegrationRestore.csproj`, not a referenced integration project.
- **Selected hosting version** means the `Aspire.Hosting` version selected for the AppHost SDK.
- **Ambient NuGet configuration** means the normal `NuGet.Config` hierarchy discovered from a directory, including user and machine configuration.
- **Policy overlay** means the small Aspire-owned `NuGet.Config` that changes source eligibility for one restore without copying the rest of the user's configuration.
- **Authoritative mapping** means competing ambient mappings are removed for a package pattern so the selected source controls that pattern.
- **Source-only policy** means a source is added without introducing a package-source-mapping overlay.

`Aspire.Hosting` intentionally participates in both restores. The package-only result supplies its runtime probe entry, while the generated root carries the selected hosting version so NuGet reports `NU1605` when a project-referenced integration requires a newer hosting version. Every other direct integration package is excluded from the generated project. Both paths apply the same Aspire-selected package source policy while preserving NuGet's native configuration behavior for credentials, trusted signers, fallback folders, audit settings, relative paths, and other user-owned settings.

## Configuration boundary

The package-only restore and the generated SDK root use the AppHost directory as their NuGet configuration boundary. Each referenced project continues to use the hierarchy discovered from its own directory.

The package-only path loads the normal hierarchy from the AppHost directory. The SDK-generated root remains under the AppHost-specific Aspire integration cache, but sets `RestoreRootConfigDirectory` to the AppHost's `.aspire` metadata directory when an Aspire policy overlay is required. Normal discovery then applies the generated `.aspire/NuGet.Config` before continuing through the AppHost directory and its ancestors. Without an overlay, `RestoreRootConfigDirectory` points directly to the AppHost directory.

The `.aspire` directory is Aspire-owned metadata. Its `NuGet.Config` path is reserved for the generated policy overlay and is not a user-owned ambient NuGet configuration location. Repository policy belongs in the AppHost directory or one of its ancestors.

This boundary intentionally:

- Includes configuration contributed by the AppHost directory and its ancestors.
- Prevents the location of Aspire's integration cache from contributing ambient NuGet policy.
- Keeps generated projects, intermediate output, and closure artifacts in the centralized integration cache.
- Keeps referenced projects responsible for their own directory-scoped configuration.

The generated root resolves the selected `Aspire.Hosting` compatibility reference and the copied-local closure for the referenced projects. It does not combine the NuGet configuration of every project in the referenced MSBuild graph.

NuGet performs one graph restore containing a restore specification for each project. Those project nodes can be processed in parallel, but each writes its own assets file and evaluates its own NuGet configuration. The generated root's restore specification uses the AppHost hierarchy and optional Aspire policy overlay, while a referenced project's restore specification uses the configuration hierarchy discovered from that project's directory. A referenced project's configuration does not modify the generated root's source policy.

The generated root's assets graph still includes packages contributed transitively by its project references. NuGet therefore resolves those transitive package identities for the root using the root's effective sources, while also restoring each referenced project using that project's effective sources. With an empty global packages folder, a package used by a referenced project may need to be available through both source policies for the complete graph restore to succeed. Once an ID and version is present in the shared global packages folder, NuGet can reuse it without consulting either source, following normal global-packages behavior.

## Effective source policy

`IntegrationRestoreSourceResolver` resolves channel and source customization before the package-only and SDK paths diverge. The service is shared by those two polyglot restore implementations; C# AppHosts do not consume it. C# AppHosts use their own `dotnet package add` flow and local or PR hive configuration behavior, including package-source mappings emitted when ambient mapping is enabled.

### Source precedence

Channel selection never changes the NuGet configuration discovery model. Every restore loads the AppHost-anchored hierarchy, then applies the same source-precedence rules:

1. An explicit source override is authoritative for the package IDs controlled by that invocation. A restore-level override controls the `Aspire*` pattern; `aspire add` controls the selected canonical package ID.
2. A selected channel with an Aspire-specific feed is authoritative for the Aspire package patterns owned by that channel.
3. Otherwise, Aspire packages use the ambient NuGet source and mapping policy.

### Channel behavior

The absence of a channel and an explicitly selected stable channel normally have the same source-resolution behavior. The exception is the invocation-local source fallback described below, which applies only when no channel is requested and the AppHost inherits the running CLI's SDK version. Stable packages do not require a dedicated Aspire feed and remain compatible with NuGet's default sources, NuGet.org mirrors, and repository-owned source policy. No AppHost-local `NuGet.Config` is required for a stable restore; when no such file exists, normal NuGet defaults and user or machine configuration apply. Daily, staging, and PR channels have channel-specific Aspire feeds, so their Aspire package mappings replace competing ambient Aspire mappings without replacing the rest of the NuGet hierarchy. The local channel is deliberately source-only: it adds the local package hive without introducing a package-source-mapping overlay.

### Channel transitions

Changing channels updates package versions and Aspire source policy as one operation. The generated policy overlay represents only the currently selected channel policy; it does not merge policy left by an earlier channel selection, and user-owned NuGet configuration is not rewritten.

When a stable project with custom NuGet configuration moves to a mapping-based source-specific channel:

- The selected channel becomes authoritative for its Aspire package patterns.
- Competing ambient Aspire mappings are temporarily replaced.
- Ambient sources and mappings for unrelated packages remain effective.
- Channel-required global-package-cache isolation prevents entries in the ordinary global packages folder from satisfying the new restore. Configured fallback folders remain part of NuGet's native policy and can still satisfy packages.

Moving back to stable removes the source-specific Aspire policy. The project's ambient Aspire mappings become effective again without requiring the user to reconstruct their NuGet configuration.

The same transition must also succeed when the project has no `NuGet.Config`. In that case, stable restore uses normal NuGet defaults, staging restore introduces the staging source through the higher-precedence Aspire policy overlay, and returning to stable removes that overlay contribution.

A project configuration is valid for stable restore when its effective sources and package-source mappings can resolve the requested stable Aspire packages and their dependencies. A repository may therefore clear default sources and use an internal mirror without changing the channel-transition model.

Selecting stable must also replace or remove any persisted non-stable channel value. Explicit stable selection uses ambient restore-source policy and must not leave the project logically pinned to daily or staging. An omitted channel can additionally use the invocation-local source fallback when restoring the running CLI's own SDK version.

### Policy result and invocation-local sources

The policy accounts for:

- The requested channel and the default stable behavior when no channel is persisted.
- Explicit source overrides.
- Local package hives.
- Staging feed overrides.
- The NuGet service-index override.
- Global-packages-folder isolation explicitly requested by the selected channel.

The result contains the effective source locations, package patterns, and cache-isolation requirements. Downstream restore paths consume this result directly and do not reconstruct built-in feed URLs.

When an AppHost has no requested channel and inherits the running CLI's SDK version, a local source associated with that CLI identity (a matching local hive or `ASPIRE_CLI_PACKAGES`) is applied as an invocation-local source override. It follows the explicit-source policy and is authoritative for the `Aspire*` package pattern, but it does not persist the running CLI's channel as project policy. An AppHost that selects a different SDK version continues to use ambient policy unless it requests a channel or source explicitly.

### `aspire add`

Polyglot `aspire add` passes the channel selected during package discovery and any explicit `--source` value into this same restore policy. It does not create or modify an AppHost-local user NuGet configuration file.

An explicit source scopes package discovery, polyglot compatibility filtering, and version selection exclusively to that source, so the command cannot offer an integration or version that the source does not contain. Without an explicit source, exact-version discovery uses each candidate channel's package-source mappings rather than performing an unscoped ambient search.

After selection, the selected canonical package ID is mapped authoritatively to the explicit source, and that source remains generally eligible for dependencies it also contains. The effective ambient and project-channel policy remains eligible for the rest of the package's dependency closure, including transitive Aspire packages that the specified source does not contain. The `--source` value and its exact package pattern are invocation-scoped; integration references do not persist a per-package restore source.

The higher-level source-scoped package discovery behavior is shared with C# AppHosts, but the restore policy and overlays described here are polyglot-specific.

### Source paths and cache isolation

Relative local sources are resolved against the AppHost directory before they are used from the integration cache.

Cache isolation is an explicit channel policy rather than something inferred from a local source path. Staging feeds opt into a source-specific global packages folder because distinct feeds can publish different packages under the same stable-shaped version. This isolates the writable global cache; it does not disable configured fallback folders.

Local and PR package hives use [NuGet's normal global-packages behavior](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders): an existing package with the requested ID and version is reused without consulting the selected source. Replacing package contents under an existing version therefore requires publishing a new version, removing the cached package, or selecting a fresh global packages folder through standard NuGet configuration.

## Native NuGet settings bridge

The bundled `Aspire.Managed` helper owns the narrow operation that requires `NuGet.Configuration`. The CLI uses this bridge instead of reproducing NuGet's configuration evaluation rules.

For a requested discovery directory, the operation:

1. Loads the normal NuGet hierarchy with `Settings.LoadDefaultSettings`.
2. Returns configuration paths in highest-to-lowest precedence order.
3. Computes an opaque cache identity from NuGet's effective package and audit sources, package-source mappings, signature-validation mode, global packages folder, fallback folders, and configuration path ordering.
4. Returns non-secret source descriptors containing the source name, enabled state, credential and client-certificate capability flags, and a per-invocation keyed identity of the resolved location.
5. Returns the effective package-source mapping entries produced by NuGet after applying the configuration hierarchy.
6. Returns disabled and reserved source keys needed to avoid accidentally inheriting name-bound credentials, certificates, or disabled state when Aspire introduces a source.
7. Returns the exact effective values of credential-bearing package and audit source locations for use only when redacting captured NuGet diagnostics.

The operation does not return `packageSourceCredentials` entries, credential-provider tokens, client certificates, trusted signers, serialized configuration sections, or standalone credential values. The cache identity represents NuGet's evaluated values rather than raw configuration file bytes or an Aspire-owned scan for environment-variable syntax.

The CLI supplies a random identity key through the helper's private process environment. Both sides use that key to calculate per-invocation HMAC source identities. This lets the CLI correlate a selected package source with an ambient alias without returning ordinary source locations from NuGet-owned configuration.

Inline credential material crosses the protocol only when it is part of a credential-bearing package or audit source location. These exact values are returned solely for redacting output captured privately between the same-user CLI and its bundled helper.

The CLI matches effective Aspire package source locations to the opaque identities using NuGet-compatible normalization rules. For each selected source, it prefers an enabled ambient alias; when every matching alias is disabled, it prefers an alias with credentials or client certificates before re-enabling one. The selected source key preserves NuGet's association with its authentication or transport settings without returning those settings to the CLI. Captured restore diagnostics are sanitized by replacing the exact credential-bearing package and audit source values and their normalized URI spellings with the same display-safe representation used for direct source arguments. Aspire does not heuristically scan arbitrary output for unknown URLs; only exact values reported by the native settings bridge are redacted. This avoids changing unrelated output and keeps URI parsing off the general process-output path.

## Aspire policy overlay

When the effective policy includes package-source mappings, the CLI writes a small `NuGet.Config` overlay.

A selected channel or explicit source override augments the effective `packageSources` set: its source is introduced when it is not already configured, while ambient sources remain available. Package eligibility is different. The effective `packageSourceMapping` policy selectively replaces ambient mappings that can tie with or outrank an authoritative selected pattern.

The overlay can contain:

- Definitions for effective sources not present in ambient settings.
- A complete effective `packageSourceMapping` policy with `<clear />`.
- Ambient mappings that do not compete with the authoritative Aspire package mappings.
- Mapping entries that refer only to NuGet source keys.
- A controlled global packages folder.
- A `disabledPackageSources` override when every ambient alias for an explicitly selected source is disabled. The overlay clears inherited disabled state, enables one selected alias, and re-emits the other disabled ambient aliases. NuGet treats the presence of an `<add>` key in this section as disabled state; the entry remains disabled even when its `value` attribute is `false`.

When ambient configuration does not enable package-source mapping, introducing an authoritative Aspire mapping must preserve the prior eligibility of ambient sources for non-Aspire packages. Selecting a channel must not implicitly restrict unrelated dependencies to the channel's fallback source.

Ambient mappings for Aspire package patterns remain effective for stable or default restores. When a source-specific channel or explicit source override is selected, only mappings that compete for those Aspire patterns are replaced.

For a selected `Aspire*` policy:

- An ambient `*` mapping can remain because the longer `Aspire*` prefix wins.
- An ambient `Aspire*` mapping must be removed because equal patterns make both sources eligible.
- Longer matching prefixes such as `Aspire.Hosting.*` and exact Aspire package IDs must be removed because they outrank `Aspire*`.
- Unrelated mappings such as `Contoso.*` remain unchanged.

The CLI composes this policy from NuGet's evaluated mapping model. It does not parse and merge each discovered configuration file independently. `Aspire.Managed` serializes the resulting source, disabled-source, mapping, and global-package-folder entries through NuGet's typed settings APIs.

The overlay never copies arbitrary user settings. Authentication, trusted signers, fallback folders, audit settings, and unknown sections continue to come from NuGet's native hierarchy loading.

## Package-only restore

The package-only path:

1. Resolves native settings from the AppHost directory.
2. Creates a temporary policy overlay at highest precedence when the effective policy requires package-source mappings.
3. Passes the ordered overlay and ambient config paths to `Aspire.Managed`.
4. Defines selected sources in the overlay when no ambient source key represents them.
5. Uses direct source arguments only for source-only policies that do not require a mapping overlay.
6. Treats the evaluated hierarchy and explicitly selected sources as the complete source set rather than implicitly appending NuGet.org.
7. Injects the resulting `ISettings` into `DependencyGraphSpecRequestProvider`.

The overlay reuses ambient source keys. `Aspire.Managed` reloads the native configuration hierarchy, so credentials, protocol settings, audit settings, and credential-provider behavior remain NuGet-owned. Only credential-bearing package and audit source location strings cross the settings bridge as exact diagnostic-redaction values.

The temporary overlay is deleted after the restore invocation.

Successful package-only restores are cached by package identity, target framework, runtime identifier, selected sources, effective NuGet settings identity, overlay identity, package-folder inputs, and the managed restore implementation. Environment-backed source and package-path changes are reflected through NuGet's evaluated settings model rather than by scanning `NuGet.Config` text.

## SDK restore root

When package-source mappings require a policy overlay, the SDK path writes `.aspire/NuGet.Config` and sets the generated root's `RestoreRootConfigDirectory` to `.aspire`. When no mapping overlay is required, including ambient-only and source-only policies, that file remains absent and `RestoreRootConfigDirectory` points to the AppHost directory instead.

The generated root contains project references plus the selected `Aspire.Hosting` version; other direct integration package references are never added to it. Keeping `Aspire.Hosting` in the graph establishes a compatibility boundary: NuGet reports `NU1605` when a referenced integration requires a newer hosting version than the AppHost selected.

At the start of each SDK restore, the overlay is deleted and regenerated or left absent so it reflects only the current invocation's policy rather than acting as durable project configuration. When present, normal SDK discovery loads the overlay together with the AppHost hierarchy. Referenced projects continue to discover configuration from their own directories.

`IntegrationRestore.csproj`, its intermediate output, and closure artifacts remain in the centralized integration cache. Their storage location does not participate in ambient NuGet configuration discovery.

The generated root receives non-empty `RestoreAdditionalProjectSources` only for a source-only policy. Otherwise, it sets the property to an empty value so an inherited environment or MSBuild property cannot introduce an untracked source. Source-specific channel and explicit override policies define their selected sources and source-key mappings in the overlay.

The generated project's early-imported props explicitly clear `RestoreConfigFile`. This prevents an inherited environment or MSBuild property from bypassing the AppHost-anchored hierarchy while preserving normal directory-based discovery.

The SDK project is always built with implicit restore. Its complete copied-local output, including project assemblies and package-backed managed, resource, and native assets selected by MSBuild, is copied into an immutable library layout. The generated AppHost server first resolves managed assembly candidates from the direct-package manifest, then from the project copied-local layout, and then from its application base directory. After finding a candidate, it defers to an assembly already available from the default load context when that assembly has an equal or higher version; `Aspire.TypeSystem` always uses the default load context. This ordering lets a direct integration package follow NuGet's global-packages resolution when the project graph also uses the same package identity and version without bypassing the server's normal version-unification rules.

## Referenced-project restore hints

The SDK process exposes two invocation-scoped MSBuild properties:

- `AspireIntegrationHostingVersion` contains the `Aspire.Hosting` version selected by the CLI.
- `AspireIntegrationPackageSources` contains the credential-free source locations selected for integration packages, formatted as an MSBuild source list.

The properties are hints for integration authors. They are visible to every project evaluated in the SDK process, including transitive project references, but Aspire does not assign the source list to NuGet restore properties for referenced projects. An integration can explicitly consume the version through central package management and append the source hint to its own `RestoreAdditionalProjectSources`.

For example, an integration that intentionally aligns its `Aspire.Hosting` dependency with the invoking CLI can use:

```xml
<Project>
  <PropertyGroup>
    <RestoreAdditionalProjectSources Condition="'$(AspireIntegrationPackageSources)' != ''">
      $(RestoreAdditionalProjectSources);$(AspireIntegrationPackageSources)
    </RestoreAdditionalProjectSources>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Aspire.Hosting" Version="$(AspireIntegrationHostingVersion)"
                    Condition="'$(AspireIntegrationHostingVersion)' != ''" />
  </ItemGroup>
</Project>
```

These properties are opt-in hints; they do not change a referenced project's restore automatically. A project-referenced hosting integration owns its package dependencies and can consume the version hint when it intentionally aligns with the invoking CLI. It can consume the source hint to make the CLI-selected feed eligible without Aspire overriding its restore policy.

A project that explicitly replaces `RestoreSources` must configure every source needed by the version it selects. Credential-bearing sources are omitted from `AspireIntegrationPackageSources` rather than redacted: a redacted URL may not identify a usable source, and copying inline credentials into the MSBuild environment would unnecessarily increase their exposure. Referenced projects execute with the same user's file access and inherit ordinary ambient environment variables, so the hint is not a security boundary for secrets already available through those mechanisms; it nevertheless does not create a new propagation path from CLI configuration into MSBuild properties. A project that needs such a source must configure it and its authentication through NuGet-owned mechanisms.

When the source policy requires an isolated global packages folder, the SDK process also receives that folder through `NUGET_PACKAGES`. The generated root and referenced projects use that cache unless a referenced project explicitly takes ownership by setting `RestorePackagesPath`.

## Credentials

Credentials remain in NuGet-owned mechanisms:

- `packageSourceCredentials`
- Environment-based credentials
- Credential providers
- Client certificates
- Authenticated ambient sources

The Aspire overlay does not copy credentials from NuGet credential sections or providers. Configured channel feed values such as `overrideStagingFeed` can contain inline URL credentials. When such a source requires a generated mapping overlay, its complete configured URL is necessarily written as the selected package-source value. NuGet credential mechanisms remain the recommended configuration.

Credential-bearing URLs supplied through `--source` are rejected before source-scoped discovery or restore, including package-version lookup. Credential-bearing sources inherited from ambient NuGet configuration remain available through NuGet's native hierarchy, as do credential-bearing configured channel sources.

Both restore paths suppress direct process logging when participating sources contain credential material and sanitize captured diagnostics through exact-value replacement. Complete HTTP or HTTPS values have user information, query strings, and fragments removed. Malformed HTTP-shaped values fail closed when their exact configured spelling appears in captured output.

NuGet-generated restore artifacts are not scrubbed or separately isolated by Aspire. Files such as `project.assets.json`, dependency graph specifications, and `.nupkg.metadata` can retain configured source URLs, including inline URL credentials. Authentication should therefore use NuGet credential mechanisms rather than embedding credentials in source URLs.

## Cache identity

Package-only cache identity includes:

- Package identities and versions.
- Target framework and runtime identifier.
- Direct source arguments selected by the invocation.
- An exact, normalized source-policy identity for isolated global package caches.
- An opaque identity computed from NuGet's effective package and audit sources, package-source mappings, signature-validation mode, and ordered configuration paths.
- Stable content identity for invocation-scoped policy overlays.
- Effective global and fallback package folder inputs.
- The managed restore implementation identity.

The SDK project-reference path does not attempt to reproduce MSBuild evaluation with directory walking or file hashes. It always runs implicit restore, allowing MSBuild and NuGet to evaluate imports, conditions, project graphs, configuration, and package versions directly. The immutable copied-local layout is reused only after the completed build describes the concrete resolved closure.

## Expected scenarios

| Scenario | Generated root | Referenced projects |
|---|---|---|
| Explicit stable channel | Uses ambient source policy and the AppHost hierarchy | Can opt into the selected version; ambient source policy remains authoritative |
| No channel | Uses ambient source policy, plus an authoritative invocation-local `Aspire*` source when needed to restore the running CLI's own SDK version | Can opt into the selected version and any credential-free invocation-local source |
| No AppHost NuGet.Config | Uses normal NuGet default, user, and machine configuration | Uses each project's normal discovery hierarchy |
| Daily, staging, or PR channel | Replaces competing Aspire mappings while retaining unrelated ambient policy | Can opt into the selected version and credential-free selected feed |
| Local channel | Adds the absolute local hive as a source-only policy without replacing ambient mappings | Can opt into the selected version and local source |
| NuGet service-index proxy override | Replaces NuGet.org only in source entries generated by the CLI; configured ambient URLs are unchanged | Retains its own restore policy |
| Explicit `--source` | Pins the invocation-owned package pattern to that source while retaining eligible ambient and channel sources for the remaining dependency closure | Can opt into the credential-free selected source |
| Credential-bearing explicit `--source` | Rejected before package discovery or restore | Not invoked |
| Credential-bearing configured channel source | Uses the configured source, suppresses raw process logging, and exact-redacts captured diagnostics | Source hint is omitted; retains its own restore policy |
| Ambient authenticated source | Uses the ambient source key and NuGet-owned credentials | Retains its own configuration and credentials |
| Explicitly selected disabled source | Clears inherited disabled-source state under the complete mapping policy | Retains its own restore policy |
| PR package hive | Uses an absolute local source with an authoritative Aspire mapping and standard NuGet global-packages behavior | Can opt into the selected version and local source |
| Nested AppHost config | Included through AppHost-anchored discovery | Remains available to projects whose own hierarchy includes it |
| Referenced project outside the AppHost tree | Uses the generated root hierarchy | Retains its own config and can explicitly consume the version and credential-free source hints |
