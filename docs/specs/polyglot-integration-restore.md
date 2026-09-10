# Polyglot integration restore

## Purpose

Polyglot AppHosts restore Aspire hosting integrations through two execution paths:

- Package-only integration closures use the bundled managed NuGet implementation.
- Closures containing project references use an SDK-generated restore project.

Both paths must apply the same Aspire-selected package source policy while preserving NuGet's native configuration behavior for credentials, trusted signers, fallback folders, audit settings, relative paths, and other user-owned settings.

## Configuration boundary

Both restore paths use the AppHost directory as their NuGet configuration boundary.

The package-only path loads the normal hierarchy from the AppHost directory. The SDK-generated root remains under the AppHost-specific Aspire integration cache, but sets `RestoreRootConfigDirectory` to the AppHost's `.aspire` metadata directory when an Aspire policy overlay is required. Normal discovery applies the generated `.aspire/NuGet.Config` before continuing through the AppHost directory and its ancestors. Without an overlay, discovery starts directly from the AppHost directory.

The `.aspire` directory is Aspire-owned metadata. Its `NuGet.Config` path is reserved for the generated policy overlay and is not a user-owned ambient NuGet configuration location. Repository policy belongs in the AppHost directory or one of its ancestors.

This boundary intentionally:

- Includes configuration contributed by the AppHost directory and its ancestors.
- Prevents the location of Aspire's integration cache from contributing ambient NuGet policy.
- Keeps generated projects, intermediate output, and closure artifacts in the centralized integration cache.
- Keeps referenced projects responsible for their own directory-scoped configuration.

The generated restore project resolves the Aspire server and integration closure. It does not combine the NuGet configuration of every project in the referenced MSBuild graph.

NuGet performs one graph restore containing a restore specification for each project. Those project nodes can be processed in parallel, but each writes its own assets file and evaluates its own NuGet configuration. The generated root's restore specification uses the AppHost hierarchy and optional Aspire policy overlay, while a referenced project's restore specification uses the configuration hierarchy discovered from that project's directory. A referenced project's configuration does not modify the generated root's source policy.

The generated root's assets graph still includes packages contributed transitively by its project references. NuGet therefore resolves those transitive package identities for the root using the root's effective sources, while also restoring each referenced project using that project's effective sources. With an empty global packages folder, a package used by a referenced project may need to be available through both source policies for the complete graph restore to succeed. Once an ID and version is present in the shared global packages folder, NuGet can reuse it without consulting either source, following normal global-packages behavior.

## Effective source policy

`IntegrationRestoreSourceResolver` resolves channel and source customization before the package-only and SDK paths diverge.

Channel selection never changes the NuGet configuration discovery model. Every restore loads the AppHost-anchored hierarchy, then applies the same source-precedence rules:

1. An explicit source override is authoritative for the package pattern owned by the invocation. Ordinary restore defaults that pattern to `Aspire*`; `aspire add` uses the selected canonical package ID.
2. A selected channel with an Aspire-specific feed is authoritative for the Aspire package patterns owned by that channel.
3. Otherwise, Aspire packages use the ambient NuGet source and mapping policy.

The absence of a channel and an explicitly selected stable channel normally have the same source-resolution behavior. The exception is the invocation-local source fallback described below, which applies only when no channel is requested and the AppHost inherits the running CLI's SDK version. Stable packages do not require a dedicated Aspire feed and remain compatible with NuGet's default sources, NuGet.org mirrors, and repository-owned source policy. No AppHost-local `NuGet.Config` is required for a stable restore; when no such file exists, normal NuGet defaults and user or machine configuration apply. Daily, staging, and PR channels have channel-specific Aspire feeds, so their Aspire package mappings replace competing ambient Aspire mappings without replacing the rest of the NuGet hierarchy. The local channel is deliberately source-only: it adds the local package hive without introducing a package-source-mapping overlay.

### Channel transitions

Changing channels updates package versions and Aspire source policy as one operation. A generated policy overlay is replaced rather than merged with the previous channel policy, and user-owned NuGet configuration is not rewritten.

When a stable project with custom NuGet configuration moves to a mapping-based source-specific channel:

- The selected channel becomes authoritative for its Aspire package patterns.
- Competing ambient Aspire mappings are temporarily replaced.
- Ambient sources and mappings for unrelated packages remain effective.
- Channel-required global-package-cache isolation prevents entries in the ordinary global packages folder from satisfying the new restore. Configured fallback folders remain part of NuGet's native policy and can still satisfy packages.

Moving back to stable removes the source-specific Aspire policy. The project's ambient Aspire mappings become effective again without requiring the user to reconstruct their NuGet configuration.

The same transition must also succeed when the project has no `NuGet.Config`. In that case, stable restore uses normal NuGet defaults, staging restore introduces the staging source through the higher-precedence Aspire policy overlay, and returning to stable removes that overlay contribution.

A project configuration is valid for stable restore when its effective sources and package-source mappings can resolve the requested stable Aspire packages and their dependencies. A repository may therefore clear default sources and use an internal mirror without changing the channel-transition model.

Selecting stable must also replace or remove a previously persisted non-stable channel value. Explicit stable selection uses ambient restore-source policy and must not leave the project logically pinned to daily or staging. An omitted channel can additionally use the invocation-local source fallback when restoring the running CLI's own SDK version.

The policy accounts for:

- The requested channel and the default stable behavior when no channel is persisted.
- Explicit source overrides.
- Local package hives.
- Staging feed overrides.
- The NuGet service-index override.
- Global-packages-folder isolation explicitly requested by the selected channel.

The result contains the effective source locations, package patterns, and cache-isolation requirements. Downstream restore paths consume this result directly and do not reconstruct built-in feed URLs.

When an AppHost has no requested channel and inherits the running CLI's SDK version, a local source associated with that CLI identity (a matching local hive or `ASPIRE_CLI_PACKAGES`) is applied as a source-only override. This keeps unpublished local and PR package versions resolvable without treating the CLI identity as the project's channel policy. An AppHost that selects a different SDK version continues to use ambient policy unless it requests a channel or source explicitly.

Polyglot `aspire add` passes the channel selected during package discovery and any explicit `--source` value into this same restore policy. It does not create or modify an AppHost-local user NuGet configuration file. An explicit source scopes package discovery, polyglot compatibility filtering, and version selection exclusively to that source, so the command cannot offer an integration or version that the source does not contain. Without an explicit source, exact-version discovery continues to use each candidate channel's package-source mappings rather than performing an unscoped ambient search. After selection, the selected canonical package ID is mapped authoritatively to an explicit source, and that source remains generally eligible for dependencies it also contains. The effective ambient and project-channel policy remains eligible for the rest of the package's dependency closure, including transitive Aspire packages that the specified source does not contain. The `--source` value and its exact package pattern remain invocation-scoped, matching the existing command contract; associating a durable restore source with an individual integration reference is follow-up design work.

The source-scoped discovery behavior is shared with C# AppHosts, but the additive restore overlay described here is polyglot-specific. C# AppHosts continue to delegate package installation to `dotnet package add --source`, because they do not use the generated polyglot restore overlay.

Relative local sources are resolved against the AppHost directory before they are used from the integration cache.

Cache isolation is an explicit channel policy rather than something inferred from a local source path. Staging feeds opt into a source-specific global packages folder because distinct feeds can publish different packages under the same stable-shaped version. This isolates the writable global cache; it does not disable configured fallback folders.

Local and PR package hives use [NuGet's normal global-packages behavior](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders): an existing package with the requested ID and version is reused without consulting the selected source. Replacing package contents under an existing version therefore requires publishing a new version, removing the cached package, or selecting a fresh global packages folder through standard NuGet configuration.

## Native NuGet settings bridge

`Aspire.Managed` owns the narrow operation that requires `NuGet.Configuration`.

For a requested discovery directory, the operation:

1. Loads the normal NuGet hierarchy with `Settings.LoadDefaultSettings`.
2. Returns configuration paths in highest-to-lowest precedence order.
3. Returns non-secret source descriptors containing the source name, enabled state, credential and client-certificate capability flags, and a per-invocation keyed identity of the resolved location.
4. Returns the effective package-source mapping entries produced by NuGet after applying the configuration hierarchy.
5. Returns disabled and reserved source keys needed to avoid accidentally inheriting name-bound credentials, certificates, or disabled state when Aspire introduces a source.
6. Returns the exact effective values of credential-bearing source locations for use only when redacting captured NuGet diagnostics.

The operation does not return `packageSourceCredentials` entries, credential-provider tokens, client certificates, trusted signers, or serialized configuration sections, and it returns no standalone credential values. The CLI supplies a random identity key through the helper's private process environment, and both sides use that key to calculate per-invocation HMAC source identities. Ordinary source locations therefore remain inside NuGet-owned configuration while the CLI can still correlate a selected source with an ambient alias. Inline credential material crosses the protocol only when it is part of a credential-bearing source location returned as an exact redaction value through the private captured-output protocol between the same-user CLI and its bundled helper.

The CLI matches effective Aspire source locations to the opaque identities using NuGet-compatible normalization rules. For each selected source, it prefers an enabled ambient alias; when every matching alias is disabled, it prefers an alias with credentials or client certificates before re-enabling one. The selected source key preserves NuGet's association with its authentication or transport settings without returning those settings to the CLI. Captured restore diagnostics are sanitized by replacing the exact credential-bearing source values and their normalized URI spellings with the same display-safe representation used for direct source arguments. Aspire does not heuristically scan arbitrary output for unknown URLs; only exact values for participating sources are redacted. This avoids changing unrelated output and keeps URI parsing off the general process-output path.

## Aspire policy overlay

When the effective policy includes package-source mappings, the CLI writes a small `NuGet.Config` overlay.

A selected channel or explicit source override augments the effective `packageSources` set: its source is introduced when it is not already configured, while ambient sources remain available. Package eligibility is different. The effective `packageSourceMapping` policy selectively replaces ambient mappings that can tie with or outrank an authoritative selected pattern.

The overlay can contain:

- Definitions for effective sources not present in ambient settings.
- A complete effective `packageSourceMapping` policy with `<clear />`.
- Ambient mappings that do not compete with the authoritative Aspire package mappings.
- Mapping entries that refer only to NuGet source keys.
- A controlled global packages folder.
- A `disabledPackageSources` override when every ambient alias for an explicitly selected source is disabled. The overlay clears inherited disabled state, enables one selected alias, and re-emits the other disabled ambient aliases.

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

The overlay reuses ambient source keys. `Aspire.Managed` reloads the native configuration hierarchy, so credentials, protocol settings, and credential-provider behavior remain NuGet-owned. Only credential-bearing source location strings cross the settings bridge as exact diagnostic-redaction values.

The temporary overlay is deleted after the restore invocation.

## SDK restore root

The SDK path writes an on-disk policy overlay to `.aspire/NuGet.Config` and sets the generated root's `RestoreRootConfigDirectory` to `.aspire`. The file is deleted and regenerated, or left absent, at the start of each SDK restore so it reflects only the current invocation's policy rather than acting as durable project configuration. Normal SDK discovery loads a generated overlay together with the AppHost hierarchy, while referenced projects continue to discover configuration from their own directories.

`IntegrationRestore.csproj`, its intermediate output, and closure artifacts remain in the centralized integration cache. Their storage location does not participate in ambient NuGet configuration discovery.

The generated root project receives non-empty `RestoreAdditionalProjectSources` only for source-only policies that do not require a mapping overlay. Otherwise, it sets the property to an empty value so an inherited environment or MSBuild property cannot introduce an untracked source. Source-specific channel and explicit override policies define their selected sources and source-key mappings in the overlay.

The generated project's early-imported props explicitly clear `RestoreConfigFile`. This prevents an inherited environment or MSBuild property from bypassing the AppHost-anchored hierarchy while preserving normal directory-based discovery.

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

The generated root always references the selected `Aspire.Hosting` package. A project-referenced hosting integration commonly references the same package itself. If the integration consumes the version hint, the root and integration restore nodes therefore resolve the same package ID and version under their respective source policies. The source hint gives the integration an explicit way to make the CLI-selected feed eligible without Aspire automatically overriding its restore policy.

Referenced projects remain responsible for their own restore policy. A project that explicitly replaces `RestoreSources` must configure every source needed by the version it selects. Credential-bearing sources are omitted from `AspireIntegrationPackageSources` rather than redacted: a redacted URL may not identify a usable source, and copying inline credentials into the MSBuild environment would unnecessarily increase their exposure. Referenced projects execute with the same user's file access and inherit ordinary ambient environment variables, so the hint is not a security boundary for secrets already available through those mechanisms; it nevertheless must not create a new propagation path from CLI configuration into MSBuild properties. A project that needs such a source must configure it and its authentication through NuGet-owned mechanisms.

When the source policy requires an isolated global packages folder, the SDK process also receives that folder through `NUGET_PACKAGES`. The generated root and referenced projects use that cache unless a referenced project explicitly takes ownership by setting `RestorePackagesPath`.

## Credentials

Credentials remain in NuGet-owned mechanisms:

- `packageSourceCredentials`
- Environment-based credentials
- Credential providers
- Client certificates
- Authenticated ambient sources

The Aspire overlay does not copy credentials from NuGet credential sections or providers. Configured channel feed values such as `overrideStagingFeed` retain the behavior supported by existing restore paths and can contain inline URL credentials. When such a source requires a generated mapping overlay, its complete configured URL is necessarily written as the selected package-source value. NuGet credential mechanisms remain the recommended configuration.

Credential-bearing URLs supplied through `--source` are rejected consistently before source-scoped discovery or restore, including package-version lookup. Credential-bearing sources inherited from ambient NuGet configuration remain available through NuGet's native hierarchy, and configured channel sources retain the behavior supported by existing restore paths. Both restore paths suppress direct process logging when participating sources contain credential material and sanitize captured diagnostics through exact-value replacement. Complete HTTP or HTTPS values have user information, query strings, and fragments removed; malformed HTTP-shaped values fail closed when their exact configured spelling appears in captured output.

NuGet-generated restore artifacts are not scrubbed or separately isolated by Aspire. Files such as `project.assets.json`, dependency graph specifications, and `.nupkg.metadata` can retain configured source URLs, including inline URL credentials. Authentication should therefore use NuGet credential mechanisms rather than embedding credentials in source URLs.

## Cache identity

Package-only cache identity includes:

- Package identities and versions.
- Target framework and runtime identifier.
- Direct source arguments selected by the invocation.
- An exact, normalized source-policy identity for isolated global package caches.
- Ordered configuration paths and file bytes.
- Stable content identity for invocation-scoped policy overlays.
- Values of environment variables referenced by NuGet config files.
- Global and fallback package folder inputs.
- The managed restore implementation identity.

SDK restore fingerprints include:

- Generated project content.
- Ordered configuration paths and file bytes.
- Integration hosting-version and credential-free package-source hint values.
- Global and fallback package folder inputs.
- Referenced project files and their directory-scoped imports.

SDK restore skipping is disabled when a config file references an environment variable, a project uses a floating or property-driven package version, or explicit MSBuild imports and property-driven project references require evaluation. Running restore again is preferred when an unchanged closure cannot be proven.

## Expected scenarios

| Scenario | Generated root | Referenced projects |
|---|---|---|
| Explicit stable channel | Uses ambient source policy and the AppHost hierarchy | Can opt into the selected version; ambient source policy remains authoritative |
| No channel | Uses ambient source policy, plus an invocation-local source when needed to restore the running CLI's own SDK version | Can opt into the selected version and any credential-free invocation-local source |
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
