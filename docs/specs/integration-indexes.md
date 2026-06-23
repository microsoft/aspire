# Aspire integration indexes

> **Status:** Draft proposal.
>
> This spec explores replacing NuGet package search as the primary Aspire integration discovery mechanism with curated integration indexes. It is informed by the current CLI implementation, [#16882](https://github.com/microsoft/aspire/pull/16882), mise's registry model, and Nix-style reproducibility goals.

## Summary

Aspire currently discovers hosting integrations by searching NuGet package metadata and filtering package IDs by naming convention:

- `Aspire.Hosting.*`
- `CommunityToolkit.Aspire.Hosting.*`

That makes NuGet package identity the user-facing integration identity. It also limits discovery to NuGet, prevents richer metadata, and makes the official/community distinction implicit in package naming.

This proposal introduces a curated integration-index layer above package providers. An integration index maps stable user-facing integration names to one or more provider coordinates such as NuGet packages, npm packages, container images, templates, or future provider types.

The first implementation should keep package installation behavior NuGet-based and reuse the existing `PackagingService`, `PackageChannel`, and `INuGetPackageCache` machinery. The index changes discovery and naming, not the package-resolution engine.

## Goals

- Decouple integration discovery from NuGet package search.
- Make official Aspire and Community Toolkit integrations first-class indexes.
- Allow future user-added indexes, such as company or community indexes, without cloning or executing arbitrary code.
- Support non-NuGet providers over time without changing the user-facing `aspire add <name>` model.
- Preserve existing Aspire package-channel semantics as index-variant resolution semantics: stable, daily, staging, PR hives, local hives, and `ASPIRE_CLI_PACKAGES`.
- Keep third-party NuGet discovery from [#16882](https://github.com/microsoft/aspire/pull/16882) as an optional dynamic discovery input.
- Give index contributions a normal repo contribution workflow: PR, schema validation, index validation, and review.
- Define integrity requirements for downloaded index artifacts so the CLI can bind cached and project-pinned indexes to a specific index revision and content digest.

## Non-goals

- Do not introduce executable provider plugins in indexes.
- Do not make the index a general-purpose package build recipe repository.
- Do not require cloning public indexes in the normal path.
- Do not replace NuGet package installation for C# AppHosts.
- Do not remove exact NuGet package ID flows.
- Do not solve provider artifact signing or package-registry trust beyond surfacing and enforcing provider-specific policy.

## Current implementation

The current CLI path is package-search driven:

```text
aspire integration list/search
aspire add
  -> IntegrationPackageSearchService
  -> PackagingService.GetChannelsAsync()
  -> PackageChannel.GetIntegrationPackagesAsync()
  -> INuGetPackageCache.GetIntegrationPackagesAsync()
  -> dotnet package search / bundled NuGet helper
  -> package ID prefix filtering
```

Important classes and responsibilities:

| Area | Current type | Responsibility |
| ---- | ------------ | -------------- |
| Integration discovery | `IntegrationPackageSearchService` | Finds NuGet packages across selected package channels and derives friendly names from package IDs. |
| Package channels | `PackagingService` | Constructs implicit, stable, daily, staging, PR hive, and local package channels. |
| Channel resolution | `PackageChannel` | Applies channel quality, pinned versions, local package directories, temporary `NuGet.config`, and exact package version lookup. |
| NuGet search | `NuGetPackageCache` | Uses `dotnet package search` and caches JSON output. |
| Bundle NuGet search | `BundleNuGetPackageCache` | Uses bundled `aspire-managed nuget search` when the CLI runs from a bundle. |
| C# install | `DotNetAppHostProject` | Calls `dotnet package add` with selected package ID, version, and optional NuGet source/feed. |
| Polyglot install | `GuestAppHostProject` | Writes package ID/version into Aspire config and regenerates SDK code. |
| MCP listing | `ListIntegrationsTool` | Has a separate integration-listing path using the default channel directly. |

The current integration identity is effectively:

```text
NuGet package ID + selected channel
```

The current friendly name is derived from the NuGet package ID:

```text
Aspire.Hosting.Redis                         -> redis
CommunityToolkit.Aspire.Hosting.SomePackage  -> communitytoolkit-somepackage
```

## Proposed model

Introduce an internal integration model:

```text
IntegrationIndex
  -> IntegrationEntry
       -> IntegrationProviderReference[]
```

Concepts:

| Concept | Example | Description |
| ------- | ------- | ----------- |
| Index ID | `aspire`, `community-toolkit`, `contoso` | The index or dynamic discovery mechanism that contributed the entry. |
| Entry ID | `redis` | Stable ID unique within an index. |
| Index-qualified ID | `aspire/redis` | Globally unique integration identity. |
| Alias | `cache` | Additional search/add shortcut. |
| Provider type | `nuget` | Built-in provider implementation used to resolve/install. |
| Provider coordinate | `nuget:Aspire.Hosting.Redis` | Ecosystem-specific artifact identity. |
| Provenance | `official`, `community`, `third-party`, `internal` | Trust/support label surfaced to users. |

This spec uses **index** for the curated integration catalog and reserves **source** for provider-specific package feeds, such as the existing `aspire add --source <nuget-feed>` option. Commands that need both concepts should keep that distinction visible in help text and output.

The first built-in indexes are:

| Index ID | Provenance | Purpose |
| --------- | ---------- | ------- |
| `aspire` | `official` | Microsoft/Aspire-supported integrations. |
| `community-toolkit` | `community` | Curated Community Toolkit integrations. |

The first provider type is:

| Provider type | Coordinate | Implementation |
| ------------- | ---------- | -------------- |
| `nuget` | `nuget:Aspire.Hosting.Redis` | Existing `PackageChannel` and `IAppHostProject.AddPackageAsync` paths. |

Future provider types can include `npm`, `container`, `template`, or `module`, but provider implementations remain built into the CLI.

## Index authoring format

Indexes should be authored as normal repo files. The exact authoring format can be YAML, JSON, or TOML; this spec uses YAML for readability. The CLI should consume a generated, validated JSON artifact.

Example entry:

```yaml
id: redis
displayName: Redis
description: Adds Redis hosting support.
aliases:
  - cache
tags:
  - cache
  - database
provenance: official
providers:
  - type: nuget
    package: Aspire.Hosting.Redis
    languages:
      - csharp
      - typescript
      - python
    versionPolicy:
      variantAware: true
      allowPrerelease: variant
```

Docs links are optional and should point to an existing first-party doc or repository README when one is available. The generated artifact should include enough metadata for the CLI to search and resolve without reading the repo tree:

```json
{
  "schemaVersion": 1,
  "index": {
    "id": "aspire",
    "displayName": "Aspire",
    "provenance": "official",
    "commit": "abc123"
  },
  "entries": [
    {
      "id": "redis",
      "displayName": "Redis",
      "aliases": ["cache"],
      "tags": ["cache", "database"],
      "minCliVersion": "13.5.0",
      "providers": [
        {
          "type": "nuget",
          "package": "Aspire.Hosting.Redis",
          "languages": ["csharp", "typescript", "python"],
          "versionPolicy": {
            "variantAware": true,
            "allowPrerelease": "variant"
          }
        }
      ],
      "docs": null
    }
  ]
}
```

`minCliVersion` is optional. When present and newer than the running CLI, the entry is treated as unavailable with a clear reason rather than being installed through partially understood metadata.

## Index acquisition

Indexes should not require a clone in the normal path.

The default acquisition order:

1. Use an embedded snapshot when no cache exists or the network is unavailable.
2. Use a cached generated index artifact when it is fresh enough.
3. Fetch a generated artifact or archive from the index endpoint.
4. Validate the artifact schema and index metadata.
5. Cache the artifact with the resolved commit or immutable version.

For GitHub-backed indexes, the preferred artifact is a generated JSON file committed in the index repository and fetched by commit SHA. A release asset or branch-generated artifact is acceptable only when the CLI can bind it to immutable content with a digest and index metadata. `aspire integration index update` advances a cached index from one resolved commit/digest to another; project-pinned indexes do not advance silently.

The embedded snapshot is a fallback, not the freshness boundary. Normal commands should prefer a valid cached remote artifact when available and can refresh built-in indexes opportunistically or through `aspire integration index update`. CLI releases should refresh the embedded snapshot so first-run/offline behavior does not drift indefinitely from the official index.

Cloning is an implementation fallback only for indexes that cannot expose a generated artifact through a simple HTTPS/GitHub raw/archive path, such as some private enterprise repositories.

When cloning is required, the CLI must:

- Use a shallow fetch.
- Disable submodules.
- Treat all files as data.
- Never execute scripts, hooks, or repo-provided code.
- Validate the generated artifact before use.

## Index management UX

Built-in indexes are available by default:

```bash
aspire integration index list
```

Example output:

```text
Name               Provenance  Location
aspire             official    built-in
community-toolkit  community   built-in
```

User-added indexes are explicit:

```bash
aspire integration index add contoso https://github.com/contoso/aspire-index
aspire integration index update
aspire integration index remove contoso
```

Project-level index pins should be recorded when a project uses a non-built-in index. This avoids silently changing integration meaning when an external index moves.

Example project provenance:

```json
{
  "integrations": {
    "indexes": [
      {
        "id": "contoso",
        "url": "https://github.com/contoso/aspire-index",
        "commit": "abc123"
      }
    ]
  }
}
```

The exact storage location is an open question because C# projects currently use NuGet project files/config while polyglot AppHosts use Aspire config.

Phase 3 cannot ship until this storage location is defined for each AppHost type. The index integrity model depends on being able to persist the index URL, resolved commit, and digest used by a project.

## Channels as index variants

The integration UX should not expose both "indexes" and "channels" as peer concepts. Existing Aspire channels are really release lanes for resolving packages from the Aspire ecosystem. In the index model, they should be represented as **variants of an index**.

Conceptually:

```text
aspire              -> default Aspire index variant
aspire@stable       -> Aspire index using stable package resolution
aspire@daily        -> Aspire index using daily package resolution
aspire@staging      -> Aspire index using staging package resolution
aspire@pr-123       -> Aspire index over a PR/local hive package resolution overlay
community-toolkit   -> Community Toolkit index using its default package resolution
contoso@prod        -> Contoso index using Contoso's production package resolution
```

An index variant owns two things:

1. The integration catalog to search.
2. The provider resolution profile used by its entries.

For NuGet-backed entries, the provider resolution profile is what today's `PackageChannel` models: feeds, package source mappings, stable/prerelease quality, pinned versions, local hives, PR hives, staging feed derivation, and `ASPIRE_CLI_PACKAGES`.

This reduces the user model to one noun:

```bash
aspire integration search redis --index aspire@daily
aspire add redis --index aspire@staging
aspire add contoso/redis
```

The current `PackageChannel` type can remain as an internal implementation detail while the CLI migrates. Public integration commands should prefer index terminology. Existing channel configuration and command-line options can be treated as compatibility aliases for selecting built-in Aspire index variants.

This does not mean every index must have every variant. `community-toolkit` may only have a default variant; an enterprise index might define `contoso@prod` and `contoso@dev`; PR/local hives can be synthesized as temporary Aspire index variants by the CLI identity/hive discovery logic.

### Built-in Aspire variant artifacts

Every Aspire index variant needs two artifact categories:

1. **Catalog artifact**: the generated Aspire integration index JSON, embedded in the CLI or fetched/cached by index commit and digest.
2. **Provider resolution artifact**: the feed, package directory, quality, pinning, and identity metadata needed by the NuGet provider to resolve the entry's package coordinate.

The catalog is usually the same across variants. The provider resolution artifact is what changes.

| Variant | Catalog artifact | Provider resolution artifact | Notes |
| ------- | ---------------- | ---------------------------- | ----- |
| `aspire` / `aspire@default` | Embedded or cached Aspire index JSON. | User's ambient NuGet configuration through the implicit channel. | Always participates so prerelease-only integrations remain reachable. |
| `aspire@stable` | Same Aspire index JSON. | `*` maps to nuget.org or `CliExecutionContext.NuGetServiceIndexOverride`; quality is stable; no pinned version. | Equivalent to today's stable channel. |
| `aspire@daily` | Same Aspire index JSON. | `Aspire*` maps to the dnceng dotnet9 feed; `*` maps to nuget.org or override; quality is prerelease. | Equivalent to today's daily channel. |
| `aspire@staging` | Same Aspire index JSON. | `Aspire*` maps to either the shared staging/daily feed or the CLI-commit-derived `darc-pub-microsoft-aspire-<commit>` feed; `*` maps to nuget.org or override; quality is stable or both; optional pinned version. | Requires CLI identity/version/commit and staging feature/config inputs to derive routing safely. |
| `aspire@pr-<N>` | Same Aspire index JSON. | Flat `.nupkg` hive at `<prefix>/hives/pr-<N>/packages` or the Aspire-home hive; `Aspire*` maps to the hive; `*` maps to nuget.org or override; quality is both; pinned version is derived from the hive contents. | A PR install can carry a co-installed hive next to `<prefix>/dogfood/pr-<N>/bin/aspire`; that hive wins over a stale same-named Aspire-home hive. |
| `aspire@local` / `aspire@run-<N>` | Same Aspire index JSON. | Flat `.nupkg` hive discovered under the Aspire hives directory; `Aspire*` maps to the hive; `*` maps to nuget.org or override; quality is both; pinned version is derived from the hive contents. | Used by localhive/dev installs and local package staging. |
| `aspire@<identity>` with `ASPIRE_CLI_PACKAGES` | Same Aspire index JSON. | Flat `.nupkg` directory from the sidecar/env `packages` value; replaces any same-named variant; `Aspire*` maps to the directory; `*` maps to nuget.org or override; quality is both; channel name follows CLI identity. | Fails fast when the directory is missing or contains multiple versions of the same `Aspire*` package ID. |

Local flat-package variants need an additional derived artifact: a **pinned version**. Today this is derived by looking for the highest version of `Aspire.ProjectTemplates`, then `Aspire.Hosting`, then `Aspire.AppHost.Sdk` in the package directory. The index model should keep that behavior so `aspire new`, `aspire add`, and package resolution stay on the same local build instead of mixing local and latest feed packages.

PR/local/package-directory variants do not need a different integration catalog unless the PR itself changes the catalog. A future PR-validation workflow could optionally publish a generated index artifact alongside PR packages; until then, the CLI can use the embedded/cached Aspire index and the PR/local provider resolution artifact.

## Resolution flow

`aspire add redis` should resolve in two phases:

```text
Integration resolution:
  redis -> aspire/redis -> nuget:Aspire.Hosting.Redis

Provider resolution:
  nuget:Aspire.Hosting.Redis -> package ID + version + NuGet source/index variant
```

Detailed flow:

1. Determine AppHost context and language.
2. Load enabled integration indexes.
3. Search entries by exact ID, alias, package coordinate, and fuzzy text.
4. Apply index precedence and ambiguity rules.
5. Select an entry.
6. Select the best provider supported by the current AppHost language.
7. Ask the provider for candidate versions.
8. Apply index-variant and version policy.
9. Install through the provider.

For the NuGet provider, step 7 should use existing `PackageChannel` methods:

```text
PackageChannel.GetPackagesAsync(packageId, workingDirectory, cancellationToken)
PackageChannel.GetPackageVersionsAsync(packageId, workingDirectory, cancellationToken)
```

This preserves today's provider-resolution behavior:

- Stable/prerelease filtering.
- Daily/staging/PR/local hive variants.
- `ASPIRE_CLI_PACKAGES` local package override.
- Pinned staging/local versions.
- Temporary `NuGet.config` generation.
- Bundle-mode NuGet search.

The implicit/default Aspire variant must always participate in NuGet provider resolution, even when an AppHost has pinned an explicit variant. This preserves the current invariant from `IntegrationPackageSearchService.GetIntegrationPackagesWithChannelsAsync`: prerelease-only integrations must remain reachable through the implicit `Quality.Both` channel even when a polyglot AppHost pins a stable-quality channel. Index `versionPolicy` filters candidates after variants have produced them; it must not narrow the variant set. This avoids regressing [#17724](https://github.com/microsoft/aspire/issues/17724) and [#17725](https://github.com/microsoft/aspire/issues/17725).

## Runtime availability

Indexes can contain entries whose provider artifact is not available from the user's active index variants. This is expected because curated metadata and feed availability are no longer the same operation.

Default behavior should preserve today's "listed means installable" expectation:

- `aspire integration list` and normal search results should include entries only when at least one supported provider has a resolvable version in the current runtime context.
- Exact or index-qualified requests, such as `aspire add aspire/foo`, should fail with a clear "integration exists but no compatible package version was found" message when the entry exists but has no available provider version.
- Future UX can add an explicit option to show unavailable entries for diagnostics, but unavailable entries should not appear as normal install choices.

Index CI validates that an entry is well-formed and that provider artifacts exist in expected publishing locations. Runtime provider resolution still validates that the selected project can actually resolve a compatible version from its configured variants and feeds.

## `--source` interaction

The existing `aspire add --source <feed>` option remains a NuGet provider option. It does not select an integration index.

Examples:

```bash
aspire add redis --source https://api.nuget.org/v3/index.json
aspire add contoso/redis --source https://pkgs.example.com/nuget/v3/index.json
```

Both commands select the integration by bare or index-qualified name first. The `--source` value is then passed to the NuGet provider as an additional package feed for version resolution and installation. Index selection uses index-qualified names (`contoso/redis`) or `aspire integration index` commands, never `--source`.

## Index and name precedence

Integration identity is index-qualified:

```text
aspire/redis
community-toolkit/redis
contoso/redis
```

Bare names are shortcuts. They must resolve deterministically or prompt.

Default precedence:

1. Exact entry ID in `aspire`.
2. Exact entry ID in other built-in curated indexes, such as `community-toolkit`.
3. Exact entry ID in user-added indexes.
4. Exact alias match using the same index precedence.
5. Fuzzy matches, prompting in interactive mode.

If multiple indexes provide the same bare name, the highest-precedence index wins for non-interactive exact matches. Interactive flows should show the index and provenance.

Non-interactive auto-selection is allowed only for exact bare-name, index-qualified, or exact provider-coordinate matches. Fuzzy matches and no-match fallbacks must continue to fail in non-interactive mode rather than silently selecting the first candidate.

Users can always qualify the index:

```bash
aspire add community-toolkit/redis
aspire add contoso/redis
```

## Multiple entries for the same package

The same provider coordinate can appear in multiple indexes under different names:

```text
aspire/redis          -> nuget:Aspire.Hosting.Redis
contoso/company-redis -> nuget:Aspire.Hosting.Redis
```

This is allowed. The index entry may carry different metadata, docs, support status, default provider preference, or policy.

The CLI should maintain a reverse lookup from provider coordinate to entries so it can:

- Explain duplicates in search results.
- Avoid adding the same package twice.
- Preserve the selected entry's provenance.
- Support exact package ID flows.

Before installing a NuGet provider coordinate, the CLI should detect whether the AppHost already references the same package ID. If it does, the command should behave idempotently and avoid adding a duplicate `PackageReference` or Aspire config package entry. Until a project-level provenance/lock file exists, the selected entry's provenance is displayed during the operation but not persisted for already-installed packages.

The provider coordinate is not the global integration identity. `index/id` is.

## Version handling

The index should express version policy, not every available package version.

Provider artifact versions remain owned by the provider ecosystem:

| Axis | Owner |
| ---- | ----- |
| Index schema version | Integration index format. |
| Integration identity | Index and entry ID. |
| Provider artifact version | NuGet, npm, container registry, or other provider. |
| Aspire compatibility | Index metadata and provider validation. |
| Installed version | Project package reference or Aspire config. |

Example policy:

```yaml
providers:
  - type: nuget
    package: Aspire.Hosting.Redis
    versionPolicy:
      variantAware: true
      compatibleAspire: "[13.0.0,14.0.0)"
      allowPrerelease: variant
```

Default NuGet version behavior:

1. Query package versions from selected index variants.
2. Filter by variant quality.
3. Filter by index compatibility metadata when present.
4. Prefer the installed CLI/SDK version for local/PR hives when available.
5. Prefer the configured variant for polyglot AppHosts when present.
6. Otherwise select the latest compatible version using semantic version precedence.

The `compatibleAspire` range is evaluated against the AppHost's resolved Aspire SDK/hosting version. C# AppHosts should use the resolved `Aspire.Hosting`/SDK version from the project. Polyglot AppHosts should use the SDK version stored in Aspire configuration or resolved by the guest AppHost project model.

Explicit versions still work:

```bash
aspire add redis --version 13.4.0
```

An explicit version should:

- Resolve against the selected provider coordinate.
- Validate that the provider artifact exists.
- Validate compatibility unless the user uses an explicit force/unsafe option.
- Preserve existing `AddCommand` behavior that searches all versions when the preferred version is not present in the initial result set.

## Trust and security model

Indexes are data, not code.

Index integrity is in scope. A downloaded index artifact must be bound to the index that produced it and to immutable content before the CLI trusts or caches it. For built-in indexes, the embedded snapshot is trusted as part of the CLI build. For remote indexes, the CLI should resolve the requested ref to a commit, validate the generated artifact, compute a content digest, and cache the artifact by index ID, commit, and digest. Project pins for non-built-in indexes should record enough information to detect drift:

```json
{
  "id": "contoso",
  "url": "https://github.com/contoso/aspire-index",
  "commit": "abc123",
  "sha256": "..."
}
```

The digest here protects the index artifact the CLI consumed. It does not replace provider-level package signing or registry trust.

Rules:

1. Indexes must not contain executable scripts, hooks, dynamic provider implementations, or arbitrary commands.
2. Provider implementations are built into the CLI.
3. Unknown provider types fail closed.
4. Entries whose providers are all unknown or unsupported are treated as unavailable rather than installable.
5. A schema version newer than the CLI understands fails closed for that index artifact. Built-in snapshots must stay on a schema supported by the shipping CLI.
6. Built-in indexes are trusted by default.
7. User-added indexes require explicit opt-in and are shown in search/add output.
8. Index ID, provenance, and provider coordinate are part of user-visible results.
9. Official index entries win name collisions by default.
10. Project use of non-built-in indexes should record index URL and commit.
11. Provider-specific trust remains provider-specific: NuGet signatures/source mapping, npm provenance, registry policy, allowlists, or future enterprise policy.
12. Remote index updates validate the index ID, schema version, resolved commit, and content digest before replacing a cached artifact.

Search and add output should make provenance visible:

```text
Name          Package                           Index              Provenance
redis         Aspire.Hosting.Redis              aspire             official
orleans-redis CommunityToolkit.Aspire.Orleans   community-toolkit  community
foo-cache     Contoso.Aspire.Hosting.FooCache   contoso            internal
```

## Relationship to NuGet tag discovery

[#16882](https://github.com/microsoft/aspire/pull/16882) adds NuGet metadata discovery for hosting integrations that do not follow the `Aspire.Hosting.*` naming convention. This proposal should compose with that work instead of replacing it.

The layered model:

```text
Curated indexes:
  "What integrations do we recommend and how should they be named?"

NuGet tag discovery:
  "What other hosting-looking NuGet packages exist?"

NuGet provider:
  "Does this package really look like an Aspire hosting integration, and what version should be installed?"
```

Potential index set:

```text
IntegrationIndex
  - AspireIndex
  - CommunityToolkitIndex
  - CustomRepoIndex
  - NuGetTagDiscoveryIndex
```

The NuGet tag discovery index should emit the same internal `IntegrationEntry` shape as curated indexes. It should mark entries as third-party and keep the validation from #16882:

- Broad discovery by NuGet tags.
- Strict validation by direct dependency on `Aspire.Hosting` or `Aspire.Hosting.AppHost`.
- Optional `off` / `ask` / `on` discovery mode.
- Optional feeds and package allowlist.

Default behavior can remain curated-only:

```bash
aspire integration search terraform
```

Opt-in third-party discovery can include NuGet tag search:

```bash
aspire integration search terraform --discovery-scope all
```

## CLI output

Human-readable list/search output should add index/provenance when the index model lands.

Proposed table:

```text
Name     Provider  Package                Version  Index   Provenance
redis    nuget     Aspire.Hosting.Redis   13.4.0   aspire  official
```

The JSON shape in `docs/specs/cli-output-formats.md` currently has:

```json
[
  {
    "name": "redis",
    "package": "Aspire.Hosting.Redis",
    "version": "13.4.0"
  }
]
```

A future shape may need:

```json
[
  {
    "name": "redis",
    "qualifiedName": "aspire/redis",
    "index": "aspire",
    "provenance": "official",
    "provider": "nuget",
    "package": "Aspire.Hosting.Redis",
    "version": "13.4.0"
  }
]
```

This is a machine-readable output contract change and must be coordinated with `docs/specs/cli-output-formats.md`, tests, and any extension/MCP consumers.

The `name` value itself may change when entries move from package-derived friendly names to curated entry IDs, especially for Community Toolkit integrations. That is a breaking change for any consumer that treats `name` as a stable command argument or documentation key. Any migration must preserve legacy names as aliases and update both integration listing and integration documentation tools together.

## MCP and VS Code extension

`ListIntegrationsTool` currently bypasses `IntegrationPackageSearchService` and lists packages directly from the default channel. The index model should move MCP integration listing to the same integration service used by CLI commands.

Integration documentation lookup must move with it. Tools such as `get_integration_docs` should accept index-qualified IDs, curated entry IDs, legacy aliases, and exact provider coordinates consistently with `aspire add`.

The VS Code extension schema may also need to understand:

- Enabled indexes.
- Third-party discovery mode.
- Per-index URL/commit pins.
- Provider-specific configuration.

## Migration plan

### Phase 1: Internal model and built-in indexes

- Add internal `IntegrationEntry`, `IntegrationProviderReference`, and `IntegrationIndex` abstractions.
- Add built-in Aspire and Community Toolkit indexes.
- Author initial index entries from existing prefix-discovered package set.
- Preserve existing user-facing names by generating aliases from both current friendly-name schemes: `IntegrationPackageSearchService.GenerateFriendlyName` and `ListIntegrationsTool.GetFriendlyName`.
- Support only NuGet providers.
- Keep existing NuGet index-variant/version/install behavior.
- Update `IntegrationPackageSearchService` to return integration candidates instead of raw packages.
- Keep exact package ID matching as a fallback.

### Phase 2: CLI UX and output

- Show index/provenance in human-readable output.
- Add index-qualified names such as `aspire/redis`.
- Update JSON output shape or introduce a new format version.
- Update MCP listing to use the shared integration service.
- Add ambiguity prompts for same name across indexes.

### Phase 3: External indexes

- Add `aspire integration index list/add/remove/update`.
- Add generated artifact acquisition and cache.
- Add schema validation and index metadata.
- Record project index provenance for non-built-in indexes.

### Phase 4: Dynamic NuGet discovery

- Integrate #16882-style NuGet tag discovery as an optional `NuGetTagDiscoveryIndex`.
- Keep third-party discovery opt-in.
- Reuse dependency metadata validation for exact package ID flows.

### Phase 5: Additional providers

- Add new built-in providers only after the index/trust model is stable.
- Candidate provider types: `npm`, `container`, `template`, `module`.
- Each provider must define version resolution, install behavior, validation, and trust policy.

## Validation

Index CI should validate:

- Schema correctness.
- Unique entry IDs within an index.
- Alias collisions and index precedence warnings.
- Provider type is known.
- Provider coordinate is syntactically valid.
- NuGet packages exist on expected feeds.
- NuGet hosting integration packages pass dependency metadata validation when available.
- Documentation links are valid.
- Deprecated entries are marked explicitly.
- Generated artifact is deterministic.

CLI tests should cover:

- Exact ID resolution.
- Alias resolution.
- Fuzzy resolution.
- Index-qualified resolution.
- Same name across indexes.
- Same package across indexes.
- Version selection across implicit and explicit variants.
- PR/local hive package selection.
- `ASPIRE_CLI_PACKAGES` package selection.
- Offline embedded snapshot behavior.
- External index schema validation failure.

## Open questions

- What authoring format should the repo use: YAML, JSON, or TOML?
- Where should official Aspire index entries live in this repo?
- Should Community Toolkit entries live in this repo, the Community Toolkit repo, or both with a generated artifact?
- Should the built-in Community Toolkit index be enabled by default or shown as curated-but-community?
- What is the project-level storage location for index pins across C# and polyglot AppHosts?
- Do we need a lock file before external indexes are allowed?
- Should `aspire add nuget:Package.Id` be an explicit provider coordinate?
- How should exact package ID fallback interact with index-qualified names?
- Should JSON output add fields in place or introduce a versioned output shape?
- What enterprise policy hooks are required before user-added indexes ship?
