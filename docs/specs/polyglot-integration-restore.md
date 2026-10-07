# Polyglot integration restore

## Purpose

A polyglot AppHost can reference Aspire hosting integrations as NuGet packages or as .NET projects. Package-only AppHosts do not require the .NET SDK, while project integrations require normal MSBuild and NuGet project-graph evaluation.

Direct package references need a NuGet restore that works without the .NET SDK and produces a manifest of package assets for the generated AppHost server. Project references need the .NET SDK to evaluate MSBuild imports, conditions, central package management, transitive project references, and copied-local output.

The restore path is selected for the complete integration set:

| Integration set | Restore owner | Output |
|---|---|---|
| Packages only | Aspire CLI's in-process NuGet client | Package probe manifest containing the resolved managed and native package assets |
| Any project references | Generated `IntegrationRestore.csproj` built by the .NET SDK | Package probe manifest for package-backed assets plus an immutable library layout for project-built output |

A mixed AppHost uses only the SDK path. Every direct integration package and project reference participates in one NuGet graph. Compatible transitive dependency ranges select one version, and irreconcilable ranges fail during restore with NuGet's normal conflict diagnostics.

In this document:

- **Generated root** means the generated `IntegrationRestore.csproj`, not a referenced integration project.
- **Selected hosting version** means the `Aspire.Hosting` version selected for the AppHost SDK.
- **Ambient NuGet configuration** means the normal `NuGet.Config` hierarchy discovered from a directory, including user and machine configuration.
- **Policy overlay** means the small Aspire-owned `NuGet.Config` that changes source eligibility for one restore without copying the rest of the user's configuration.
- **Authoritative mapping** means competing ambient mappings are removed for a package pattern so the selected source controls that pattern.
- **Source-only policy** means a source is added without introducing a package-source-mapping overlay.

Both paths apply the same Aspire-selected package source policy while preserving NuGet's native configuration behavior for credentials, trusted signers, audit settings, relative paths, the global packages folder, and other supported user-owned settings. When the SDK path is required, the generated root includes the selected `Aspire.Hosting` version together with every other direct integration package, so ordinary NuGet graph resolution also enforces hosting-version compatibility.

## Configuration boundary

The package-only restore and the generated SDK root use the AppHost directory as their NuGet configuration boundary. Each referenced project continues to use the hierarchy discovered from its own directory.

The package-only path loads the normal hierarchy from the AppHost directory. The SDK-generated root remains under the AppHost-specific Aspire integration cache, but sets `RestoreRootConfigDirectory` to the AppHost's `.aspire` metadata directory when an Aspire policy overlay is required. Normal discovery then applies the generated `.aspire/NuGet.Config` before continuing through the AppHost directory and its ancestors. Without an overlay, `RestoreRootConfigDirectory` points directly to the AppHost directory.

The `.aspire` directory is Aspire-owned metadata. Its `NuGet.Config` path is reserved for the generated policy overlay and is not a user-owned ambient NuGet configuration location. Repository policy belongs in the AppHost directory or one of its ancestors.

This boundary intentionally:

- Includes configuration contributed by the AppHost directory and its ancestors.
- Prevents the location of Aspire's integration cache from contributing ambient NuGet policy.
- Keeps generated projects, intermediate output, and closure artifacts in the centralized integration cache.
- Keeps referenced projects responsible for their own directory-scoped configuration.

The generated root resolves every direct integration package together with the copied-local closure for the referenced projects. It does not combine the NuGet configuration of every project in the referenced MSBuild graph.

NuGet performs one graph restore containing a restore specification for each project. Those project nodes can be processed in parallel, but each writes its own assets file and evaluates its own NuGet configuration. The generated root's restore specification uses the AppHost hierarchy and optional Aspire policy overlay, while a referenced project's restore specification uses the configuration hierarchy discovered from that project's directory. A referenced project's configuration does not modify the generated root's source policy.

The generated root's assets graph still includes packages contributed transitively by its project references. NuGet therefore resolves those transitive package identities for the root using the root's effective sources, while also restoring each referenced project using that project's effective sources. With an empty global packages folder, a package used by a referenced project may need to be available through both source policies for the complete graph restore to succeed. Once an ID and version is present in the shared global packages folder, NuGet can reuse it without consulting either source, following normal global-packages behavior.

## Effective source policy

`IntegrationRestorePlanResolver` resolves channel, source, and ambient NuGet settings before selecting the package-only or SDK path. It returns an immutable `IntegrationRestorePlan` that owns the resolved data and the standard package-path and project-path configuration projections. The paths therefore apply the same source policy even though they use different restore implementations and output models. C# AppHosts do not consume this plan; they use their own `dotnet package add` flow and local or PR hive configuration behavior, including package-source mappings emitted when ambient mapping is enabled.

### Source precedence

Channel selection never changes the NuGet configuration discovery model. Every restore loads the AppHost-anchored hierarchy, then applies the same source-precedence rules:

1. An explicit source override is authoritative for the package IDs controlled by that invocation. A restore-level override controls the `Aspire*` pattern; `aspire add` controls the selected canonical package ID and appends the source to the feeds eligible for the remaining Aspire closure.
2. A selected channel with an Aspire-specific feed is authoritative for the Aspire package patterns owned by that channel.
3. Otherwise, Aspire packages use the ambient NuGet source and mapping policy.

### Channel behavior

The absence of a channel and an explicitly selected stable channel normally have the same source-resolution behavior. The exception is the invocation-local source fallback described below, which applies only when no channel is requested and the AppHost inherits the running CLI's SDK version. Stable packages do not require a dedicated Aspire feed and remain compatible with NuGet's default sources, NuGet.org mirrors, and repository-owned source policy. An ambient-only stable selection requires no temporary policy overlay and removes any previous generated-root overlay. Explicit source overrides still require their selected policy. Daily, staging, and PR channels have channel-specific Aspire feeds, so their Aspire package mappings replace competing ambient Aspire mappings without replacing the rest of the NuGet hierarchy. A hive-backed local channel similarly emits an authoritative `Aspire*` mapping to the local package directory while retaining unrelated ambient mappings.

### Channel transitions

Changing channels updates package versions and Aspire source policy as one operation. The generated policy overlay represents only the currently selected channel policy; it does not merge policy left by an earlier channel selection, and user-owned NuGet configuration is not rewritten.

When a stable project with custom NuGet configuration moves to a mapping-based source-specific channel:

- The selected channel becomes authoritative for its Aspire package patterns.
- Competing ambient Aspire mappings are temporarily replaced.
- Ambient sources and mappings for unrelated packages remain effective.
- Channel-required global-package-cache isolation prevents entries in the ordinary global packages folder from satisfying the new restore.

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

An explicit source scopes package discovery, polyglot compatibility filtering, and version selection exclusively to that source, so the command cannot offer an integration or version that the source does not contain. Its temporary overlay disables unselected source aliases while inheriting the selected alias's credentials, client certificates, and transport settings. Without an explicit source, ordinary and exact-version discovery enforce each candidate channel's effective package-source mappings, including relevant ambient mappings.

After selection, the selected canonical package ID is mapped authoritatively to the explicit source. The source is also mapped for `Aspire*` and general dependencies, while the effective ambient, identity, and project-channel policy remains eligible for the rest of the package's dependency closure. The command therefore appends a package feed rather than treating it as a complete package universe. If the same Aspire package ID and version exists in multiple eligible feeds, NuGet does not provide source priority; the package can come from either feed or an existing global-packages entry.

The `--source` value and its package mappings are invocation-scoped. Aspire does not currently persist per-integration channel or source provenance in `aspire.config.json`. The add can therefore succeed while a later cold restore cannot resolve the selected package version through the persisted project policy. A corporate mirror that must replace a blocked default feed likewise requires persistent NuGet or project policy; an invocation-scoped appended source does not prevent NuGet from contacting another eligible feed, and an inaccessible feed can still fail restore.

The selected channel is likewise used only for the add invocation; selecting a package from another channel does not replace the channel persisted for the AppHost. Persisting the selected channel automatically would instead change source policy for every integration in the AppHost.

The higher-level source-scoped package discovery behavior is shared with C# AppHosts, but the restore policy and overlays described here are polyglot-specific.

### Source paths and cache isolation

Relative local sources are resolved against the AppHost directory before they are used from the integration cache.

Cache isolation is an explicit channel policy rather than something inferred from a local source path. Staging feeds opt into a source-specific global packages folder because distinct feeds can publish different packages under the same stable-shaped version. This isolates the writable global cache.

Local and PR package hives use [NuGet's normal global-packages behavior](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders): an existing package with the requested ID and version is reused without consulting the selected source. Replacing package contents under an existing version therefore requires publishing a new version, removing the cached package, or selecting a fresh global packages folder through standard NuGet configuration.

## In-process NuGet settings

The CLI uses `NuGet.Configuration` directly instead of reproducing NuGet's configuration evaluation rules.

For a requested discovery directory, the operation:

1. Loads the normal NuGet hierarchy with `Settings.LoadDefaultSettings`, including the cross-platform machine-wide settings provider used by package restore and search.
2. Returns configuration paths in highest-to-lowest precedence order.
3. Computes an opaque cache identity from NuGet's effective package and audit sources, package-source mappings, signature-validation mode, trusted signers, the global packages folder, and configuration path ordering.
4. Returns non-secret source descriptors containing the source name, enabled state, credential and client-certificate capability flags, and a per-invocation keyed identity of the resolved location.
5. Returns the effective package-source mapping entries produced by NuGet after applying the configuration hierarchy.
6. Returns disabled and reserved source keys needed to avoid accidentally inheriting name-bound credentials, certificates, or disabled state when Aspire introduces a source.
7. Returns the exact effective values of credential-bearing package and audit source locations for use only when redacting captured NuGet diagnostics.

The operation does not expose `packageSourceCredentials` entries, credential-provider tokens, client certificates, trusted signers, serialized configuration sections, or standalone credential values. The cache identity represents NuGet's evaluated values rather than raw configuration file bytes or an Aspire-owned scan for environment-variable syntax.

The CLI creates a random identity key for each settings snapshot and uses it to calculate per-invocation HMAC source identities. This lets the restore-policy layer correlate a selected package source with an ambient alias without retaining ordinary source locations from NuGet-owned configuration.

Source identity canonicalizes URI spelling without folding URI path case. Fully qualified local paths and file URIs share an identity; local path case is folded only on Windows. Explicit-source mapping composition deduplicates equivalent source-and-pattern pairs before computing the isolated-cache identity, comparing package patterns case-insensitively and preserving the first source spelling for configuration output.

Inline credential material is retained only when it is part of a credential-bearing package or audit source location. These exact values and the credential-bearing URI components extracted from them are used solely for redacting NuGet diagnostics before they are logged or surfaced.

The CLI matches effective Aspire package source locations to the opaque identities using NuGet-compatible normalization rules. For each selected source, it prefers an enabled ambient alias; when every matching alias is disabled, it prefers an alias with credentials or client certificates before re-enabling one. The selected source key preserves NuGet's association with its authentication or transport settings without exposing those settings to the restore-policy layer. Captured restore diagnostics are sanitized by replacing the exact credential-bearing package and audit source values, their normalized URI spellings, and delimiter-qualified user-info, query-parameter, and fragment components extracted from those values. This follows the [.NET HTTP logging privacy boundary](https://learn.microsoft.com/dotnet/core/compatibility/networking/9.0/query-redaction-logs), which treats user information, query strings, and fragments as sensitive. Component matching protects identified credentials when NuGet reports another protocol resource URL without replacing bare credential values in unrelated text. Aspire does not heuristically scan arbitrary output for unknown URLs and cannot discover independently issued credentials that appear only in server-provided diagnostics. This avoids changing unrelated output and keeps URI parsing off the general process-output path.

## Aspire policy overlay

When the effective policy includes package-source mappings, the CLI writes a small `NuGet.Config` overlay.

Configuration responsibilities are separated by what they own:

| Type or layer | Responsibility |
|---|---|
| Packaging and restore policy | Select channel/source intent, package patterns, and cache-isolation requirements. `Packaging/IntegrationPackageSearchService` coordinates shared integration discovery and matching rather than belonging to a command. |
| `BundleNuGetService` | Load the evaluated snapshot through the native client, invoke composition, and materialize configuration for an operation. Independently, perform package restores, coordinate reusable restore-cache locking and reuse, and generate package probe manifests. |
| `NuGetConfigurationBuilder` | Purely compose selected source aliases and the effective overlay from the snapshot and selected policy. It does not load settings, create files, or hold a service reference. |
| `NuGetConfiguration` | Hold the resolved snapshot, aliases, and already-composed overlay. It has no service reference, configuration-loading methods, or file-writing methods. |
| Operation and restore descriptors | Carry execution directories, config paths, and cache identities, and own temporary-overlay lifetimes. |
| `NuGetClient` | Own full native settings and protocol objects, evaluate search eligibility with NuGet's mapping rules, serialize overlays, and execute native restore/search. Credentials and native authentication behavior stay here. |

The service is not a wrapper around the configuration value. Its package-restore entry point works with execution inputs and does not require a `NuGetConfiguration`; the configuration is needed only by composition and materialization operations. Combining them would make a passive policy result responsible for restore execution, cache concurrency, and file lifetime.

Stable channel operations contribute no selected source policy and use ambient NuGet configuration directly. Other channels contribute only their Aspire-specific mappings; their synthetic `*` fallback is not copied into the operation overlay, so ambient configuration continues to own unrelated package sources. SDK-driven package searches and template installation place the temporary overlay below the caller's working directory so normal NuGet hierarchy discovery still loads repository and user configuration.

NuGet package search does not enforce package-source mapping. Both bundled and SDK-driven discovery therefore apply NuGet's native mapping evaluator to search results. Bundled discovery applies eligibility before cross-source deduplication, so an ineligible feed's higher version cannot discard an eligible lower version. SDK-driven discovery evaluates per-source results for both ordinary and exact-version searches; source names or locations are resolved to native source aliases inside `NuGetClient`.

A selected channel or explicit source override augments the effective `packageSources` set: its source is introduced when it is not already configured, while ambient sources remain available. Package eligibility is different. The effective `packageSourceMapping` policy selectively replaces ambient mappings that can tie with or outrank an authoritative selected pattern. A package-scoped `aspire add --source` mapping keeps the exact selected package authoritative while mapping both the appended source and the resolved identity or channel feed to `Aspire*`.

The overlay can contain:

- Definitions for effective sources not present in ambient settings.
- A complete effective `packageSourceMapping` policy with `<clear />`.
- Ambient mappings that do not compete with the authoritative Aspire package mappings.
- Mapping entries that refer only to NuGet source keys.
- A controlled global packages folder.
- A `disabledPackageSources` override when every ambient alias for an explicitly selected source is disabled. The overlay clears inherited disabled state, enables one selected alias, and re-emits the other disabled ambient aliases. NuGet treats the presence of an `<add>` key in this section as disabled state; the entry remains disabled even when its `value` attribute is `false`.

Source-restricted discovery and template installation use this same composer, with a restriction that disables every unselected alias and removes its mapping entries. Removing those entries prevents an unselected source's exact package mapping from outranking the selected source's `*` mapping. This restriction is part of the resolved overlay, not a file-writer option, so temporary, generated-root, and regenerated writers all materialize the same policy. Restore's additive dependency-source behavior is unchanged.

When a selected source is not already represented by an ambient alias, Aspire namespaces its generated key with the same stable workload identifier derived from the AppHost path for DCP. The primary selected source prefers `aspire-<workload-id>`. Additional generated sources, and primary sources whose preferred key is reserved, use `aspire-<workload-id>-0`, `aspire-<workload-id>-1`, and so on, skipping all reserved keys. This permits channel transitions without overwriting an earlier channel's alias or inheriting name-bound credentials and certificates. Sources already represented by ambient aliases continue to use those aliases so NuGet-owned credentials and client certificates remain associated with the correct keys.

When ambient configuration does not enable package-source mapping, introducing an authoritative Aspire mapping must preserve the prior eligibility of ambient sources for non-Aspire packages. Selecting a channel must not implicitly restrict unrelated dependencies to the channel's fallback source.

Ambient mappings for Aspire package patterns remain effective for stable or default restores. A restore-level `Aspire*` override replaces mappings that compete for that broad pattern. An exact package-scoped override replaces only mappings that can control the selected package; general Aspire mappings remain and the appended source receives its own `Aspire*` mapping.

For a selected `Aspire*` policy:

- An ambient `*` mapping can remain because the longer `Aspire*` prefix wins.
- An ambient `Aspire*` mapping must be removed because equal patterns make both sources eligible.
- Longer matching prefixes such as `Aspire.Hosting.*` and exact Aspire package IDs must be removed because they outrank `Aspire*`.
- Unrelated mappings such as `Contoso.*` remain unchanged.

The CLI composes this policy from NuGet's evaluated mapping model. It does not parse and merge each discovered configuration file independently. The in-process NuGet client serializes the resulting source, disabled-source, mapping, and global-package-folder entries through NuGet's typed settings APIs.

The overlay never copies arbitrary user settings. Authentication, trusted signers, audit settings, and unknown sections continue to come from NuGet's native hierarchy loading.

Package-only restores do not currently consume NuGet fallback package folders from configuration or `NUGET_FALLBACK_PACKAGES`. Complete support requires both restore-time folder selection and manifest asset resolution across the ordered package folders, and is tracked by [#20631](https://github.com/microsoft/aspire/issues/20631).

### Shared policy previews and .NET persistence

Both .NET and polyglot AppHosts use `BundleNuGetService` and `NuGetConfigurationBuilder` to compute the desired effective policy before creating any configuration file. The native client's `WriteNuGetConfig` serializes that policy through NuGet's typed settings APIs. `TemporaryNuGetConfigFile` owns only materialization, cache identity, and cleanup; it does not select channel policy or plan durable changes.

`BundleNuGetService.CreateConfigurationPreviewAsync` materializes the shared desired policy under the caller's `.aspire` directory. NuGet evaluates this overlay together with the unchanged ambient hierarchy. Relative paths and name-bound authentication and transport settings remain owned by their original configuration files. An ambient-only policy requires no overlay.

Without an explicit `--channel` or legacy `--quality` option, .NET updates discover packages through the current native NuGet policy and never change configured feeds. Project or global Aspire channel metadata, the running CLI's identity, and available PR hives cannot select replacement feeds for an unqualified .NET update.

An explicit channel option permits composing a desired policy, not an unconditional rewrite. The CLI compares evaluated sources, mappings, and disabled-source state rather than channel names. Equivalent policy preserves existing file bytes and uses ambient discovery without an overlay; a repeated channel name can still require different feeds. A requested staging cache-folder change is evaluated separately.

When policy differs, explicit-channel .NET update discovery consumes the preview, including SDK discovery when an existing channel mapping excludes the target feed. The update retains the same composed policy for persistence rather than rebuilding it after package selection. Declining the configuration candidate stops the update before file edits. Feed-only changes still require native restore even when package versions are unchanged.

Before composing an explicitly requested .NET policy, the CLI evaluates the current AppHost's MSBuild restore-policy properties without executing targets or restoring its project-reference graph. Restore targets can run legacy SDK validation before the updater repairs the project; that validation belongs to the full candidate restore after edits are staged. Explicit channel policy is refused when `RestoreConfigFile`, `RestoreRootConfigDirectory`, `RestoreSources`, `RestoreAdditionalProjectSources`, `_RestoreSourcesOverride`, or a non-default `NuGetRestoreTargets` overrides ambient configuration. Unknown or overridden restore policy cannot be safely compared with the desired ambient policy, so explicit requests fail closed. Unqualified updates skip this inspection, preserve customized settings, and retain the existing fallback parser for SDK repair. Cancellation never enters that fallback.

Only .NET adds an existing-file persistence transaction. `DotNetAppHostNuGetConfigMerger` consumes the same already-composed policy, asks the common writer to preserve unrelated content from the target file, and freezes the original and proposed bytes in a `DotNetAppHostNuGetConfigMergerCandidate`. Apply refuses to overwrite a file changed or created after preparation. Policy composition disables retired CLI hives and sources whose mappings have all been reassigned; the common writer also removes their local source definitions when serializing an existing file.

After confirmation, .NET update acquires the selected SDK through the candidate policy into the original ambient SDK resolver's package cache. This is SDK acquisition without application assets, not restoration of the old application graph. It stages all SDK, project, and central package version edits in place without per-package restore, then runs full native restore of the updated graph. Using the real files preserves project identities, relative imports, and central package management.

`DotNetAppHostRestorePreview` evaluates the frozen configuration replacement at native precedence for each affected project. An invocation-scoped targets file projects those policies into native restore, retaining more-local child configuration, explicit child restore/cache properties, and child-specific restore targets. The selected configuration must be in the AppHost directory or an ancestor. NuGet configuration and Aspire metadata/tool changes are committed only after candidate restore succeeds; ordinary `dotnet restore` then uses the persisted policy without a preview.

Handled failures and cancellation restore owned project, central package, configuration, metadata, and tool-file edits. Concurrently changed files are not overwritten, and rollback failures are surfaced together with the original failure. This is not crash-safe or filesystem-wide atomicity: package downloads and generated restore outputs can remain after rollback. Metadata/tool-only changes do not trigger an unrelated application restore.

Initial template creation remains separate from existing-AppHost updates: non-stable channels and explicit `--source` selections persist a new configuration through the common writer for all languages. Stable template creation does not create a configuration. Existing .NET configuration can be updated for stable without creating a missing file.

## Package-only restore

The package-only path is used only when the integration set contains no project references. It:

1. Uses the native settings snapshot resolved from the AppHost directory by the integration restore plan.
2. Creates a temporary policy overlay at highest precedence when the effective policy requires package-source mappings.
3. Passes the ordered overlay and ambient config paths to the in-process NuGet client.
4. Defines selected sources in the overlay when no ambient source key represents them.
5. Uses direct source arguments only for source-only policies that do not require a mapping overlay.
6. Treats the evaluated hierarchy and explicitly selected sources as the complete source set rather than implicitly appending NuGet.org.
7. Injects the resulting `ISettings` into `DependencyGraphSpecRequestProvider`.

The overlay reuses ambient source keys. The NuGet client reloads the native configuration hierarchy, so credentials, protocol settings, audit settings, and credential-provider behavior remain NuGet-owned. Only credential-bearing package and audit source location strings are retained as exact diagnostic-redaction values.

The temporary overlay is deleted after the restore invocation.

Successful package-only restores are cached by package identity, target framework, runtime identifier, selected sources, effective NuGet settings identity, overlay identity, package-folder inputs, and the CLI NuGet restore implementation. Environment-backed source and package-path changes are reflected through NuGet's evaluated settings model rather than by scanning `NuGet.Config` text.

## SDK restore root

When package-source mappings require a policy overlay, the SDK path writes `.aspire/NuGet.Config` and sets the generated root's `RestoreRootConfigDirectory` to `.aspire`. When no mapping overlay is required, including ambient-only and source-only policies, that file remains absent and `RestoreRootConfigDirectory` points to the AppHost directory instead.

The generated root contains every direct integration package and project reference. This produces one NuGet graph for mixed AppHosts: compatible transitive package ranges select one effective version, while irreconcilable ranges fail during restore with the normal NuGet diagnostics.

At the start of each SDK restore, the overlay is deleted and regenerated or left absent so it reflects only the current invocation's policy rather than acting as durable project configuration. When present, normal SDK discovery loads the overlay together with the AppHost hierarchy. Referenced projects continue to discover configuration from their own directories.

The generated root overlay is not a reusable additional configuration file for referenced projects. It contains the complete effective source-key and package-source-mapping policy composed for the generated root, including relevant mappings inherited from the AppHost hierarchy and any `<clear />` needed to make the selected policy authoritative. Directing a referenced project to use this file could replace or reinterpret policy from that project's own hierarchy. Aspire therefore does not expose the overlay as a `RestoreConfigFile` hint for referenced projects.

`IntegrationRestore.csproj`, its intermediate output, and closure artifacts remain in the centralized integration cache. Their storage location does not participate in ambient NuGet configuration discovery.

The generated root receives non-empty `RestoreAdditionalProjectSources` only for a source-only policy. Otherwise, it sets the property to an empty value so an inherited environment or MSBuild property cannot introduce an untracked source. Source-specific channel and explicit override policies define their selected sources and source-key mappings in the overlay.

The generated project's early-imported props explicitly clear `RestoreConfigFile`. This prevents an inherited environment or MSBuild property from bypassing the AppHost-anchored hierarchy while preserving normal directory-based discovery.

The SDK project is always built with implicit restore. The generated root targets the current host runtime identifier so NuGet selects only the applicable runtime-specific package assets; this project-local setting is not imposed on referenced projects. Package-backed managed, resource, and native assets selected by the unified graph are represented in the package probe manifest and remain in NuGet's package folders. Project-built output is copied into an immutable library layout. The generated AppHost server first resolves managed assembly candidates from the package manifest, then from the project copied-local layout, and then from its application base directory. Because both outputs originate from one restore graph, an assembly identity shared by package and project integrations has already been reconciled by NuGet. After finding a candidate, the server defers to an assembly already available from the default load context when that assembly has an equal or higher version; `Aspire.TypeSystem` always uses the default load context.

## Referenced-project restore hints

The SDK process exposes three invocation-scoped MSBuild properties:

- `AspireIntegrationHostingVersion` contains the `Aspire.Hosting` version selected by the CLI.
- `AspireIntegrationPackageSources` contains the credential-free source locations selected for integration packages, formatted as an MSBuild source list.
- `AspireIntegrationPackageSourceAlias` contains the source key used by the generated root for the primary source in `AspireIntegrationPackageSources`. It preserves a selected ambient alias or contains the workload-derived alias when Aspire introduces the source.

The properties are hints for integration authors. They are visible to every project evaluated in the SDK process, including transitive project references, but Aspire does not assign the source list to NuGet restore properties for referenced projects. An integration can explicitly consume the version through central package management. It can append the source hint to its own `RestoreAdditionalProjectSources` when its effective NuGet policy does not require additional package-source-mapping configuration.

For example, an integration that intentionally aligns its `Aspire.Hosting` dependency with the invoking CLI and can consume the selected sources under its existing mapping policy can use:

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

These properties are opt-in hints; they do not change a referenced project's restore automatically. A project-referenced hosting integration owns its package dependencies and can consume the version hint when it intentionally aligns with the invoking CLI.

`AspireIntegrationPackageSources` contains source locations and does not carry package-source mappings. `AspireIntegrationPackageSourceAlias` identifies only the generated root's key for the primary hinted source; additional hinted sources do not currently expose corresponding aliases. `RestoreAdditionalProjectSources` likewise adds locations without adding mapping configuration. When package-source mapping is enabled for a referenced project, an appended source is eligible only if that project's normally discovered configuration maps the source key NuGet associates with it. A project can use the alias hint when it intentionally constructs its own invocation-scoped mapping configuration, but Aspire does not generate or inject that configuration for referenced projects.

NuGet exposes `RestoreConfigFile` as a replacement for normal configuration discovery, not as an additional file to merge after the project's discovered hierarchy. Using it for an Aspire-generated file could bypass configuration selected by the project or its imported build logic. The generated root overlay is also unsuitable for this purpose because it represents the AppHost root's composed effective policy rather than a source-specific fragment. Aspire therefore cannot currently contribute additional mapping configuration while preserving the referenced project's normal NuGet configuration behavior.

A project that explicitly replaces `RestoreSources` must configure every source needed by the version it selects. Credential-bearing sources are omitted from `AspireIntegrationPackageSources` rather than redacted: a redacted URL may not identify a usable source, and copying inline credentials into the MSBuild environment would unnecessarily increase their exposure. The corresponding alias property is also omitted when no source hint can be exposed. Referenced projects execute with the same user's file access and inherit ordinary ambient environment variables, so the hint is not a security boundary for secrets already available through those mechanisms; it nevertheless does not create a new propagation path from CLI configuration into MSBuild properties. A project that needs such a source must configure it and its authentication through NuGet-owned mechanisms.

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

Package-only restores suppress direct process logging when participating sources contain credential material and sanitize captured diagnostics using the identified source values and their credential-bearing URI components. Complete HTTP or HTTPS values have user information, query strings, and fragments removed. Malformed HTTP-shaped values fail closed when their exact configured spelling appears in captured output. Credential-provider and trust-store diagnostics are redacted against the reference-counted union of sensitive sources owned by active NuGet operations because overlapping operations share NuGet's process-wide credential service.

NuGet-generated restore artifacts are not scrubbed or separately isolated by Aspire. Files such as `project.assets.json`, dependency graph specifications, and `.nupkg.metadata` can retain configured source URLs, including inline URL credentials. Authentication should therefore use NuGet credential mechanisms rather than embedding credentials in source URLs.

Generated SDK builds suppress raw process logging. If a build fails, the CLI reads the generated dependency graph specification, collects credential-bearing package sources from every evaluated project, and sanitizes those sources and their identified credential components before returning or logging the captured diagnostics. If that graph is unavailable or malformed, the CLI omits the raw diagnostics and returns only the safe build-failure summary.

## Cache identity

Package-only cache identity includes:

- Package identities and versions.
- Target framework and runtime identifier.
- Direct source arguments selected by the invocation.
- An exact, normalized source-policy identity for isolated global package caches.
- An opaque identity computed from NuGet's effective package and audit sources, package-source mappings, signature-validation mode, trusted signers, and ordered configuration paths.
- Stable content identity for invocation-scoped policy overlays.
- The effective global package folder input.
- The managed restore implementation identity.

The SDK project-reference path does not attempt to reproduce MSBuild evaluation with directory walking or file hashes. It always runs implicit restore, allowing MSBuild and NuGet to evaluate imports, conditions, project graphs, configuration, and package versions directly. This deliberately favors correctness over warm-build performance. Safely skipping restore would require a complete model of every input consumed by MSBuild and NuGet, including imported projects, props and targets, environment-derived configuration, the evaluated project graph, external sources, and mutable package caches. An approximate fingerprint can both miss meaningful changes and invalidate unnecessarily. Package-only restores retain their reusable cache; the less common project-reference path restores before each build. The immutable copied-local layout is reused only after the completed build describes the concrete resolved closure.

## Expected scenarios

| Scenario | Generated root | Referenced projects |
|---|---|---|
| Explicit stable channel | Uses ambient source policy and the AppHost hierarchy | Can opt into the selected version; ambient source policy remains authoritative |
| No channel | Uses ambient source policy, plus an authoritative invocation-local `Aspire*` source when needed to restore the running CLI's own SDK version | Can opt into the selected version; the source hint requires a compatible project mapping policy |
| No AppHost NuGet.Config | Uses normal NuGet default, user, and machine source, mapping, authentication, trust, audit, and global-packages configuration | Uses each project's normal discovery hierarchy |
| Daily, staging, or PR channel | Replaces competing Aspire mappings while retaining unrelated ambient policy | Can opt into the selected version; the source hint requires a compatible project mapping policy |
| Local channel | Maps `Aspire*` to the absolute local hive while retaining unrelated ambient mappings | Can opt into the selected version; the local source hint requires a compatible project mapping policy |
| NuGet service-index proxy override | Does not add or rewrite restore sources; configured ambient URLs remain authoritative | Retains its own restore policy |
| Restore-level explicit `--source` | Maps `Aspire*` exclusively to the explicit source while retaining unrelated ambient policy | Receives a credential-free source hint that requires a compatible project mapping policy |
| Explicit `aspire add --source` | Pins the selected package to that source and makes both the appended source and resolved identity, channel, or ambient feeds eligible for the remaining Aspire closure; source priority is not guaranteed | Receives a credential-free source hint that requires a compatible project mapping policy |
| Credential-bearing explicit `--source` | Rejected before package discovery or restore | Not invoked |
| Credential-bearing configured channel source | Uses the configured source, suppresses raw process logging, and exact-redacts captured diagnostics | Source hint is omitted; retains its own restore policy |
| Ambient authenticated source | Uses the ambient source key and NuGet-owned credentials | Retains its own configuration and credentials |
| Explicitly selected disabled source | Clears inherited disabled-source state under the complete mapping policy | Retains its own restore policy |
| PR package hive | Uses an absolute local source with an authoritative Aspire mapping and standard NuGet global-packages behavior | Can opt into the selected version; the local source hint requires a compatible project mapping policy |
| Nested AppHost config | Included through AppHost-anchored discovery | Remains available to projects whose own hierarchy includes it |
| Referenced project outside the AppHost tree | Uses the generated root hierarchy | Retains its own config; can consume the version hint and only those source hints permitted by its mapping policy |
