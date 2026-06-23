# Aspire integration indexes

> **Status:** Draft proposal.
>
> This spec explores replacing NuGet package search as the primary Aspire integration discovery mechanism with curated integration indexes. It is informed by the current CLI implementation, [#16882](https://github.com/microsoft/aspire/pull/16882), mise's registry model, and reproducibility goals from package-index ecosystems.

## Summary

Aspire currently discovers hosting integrations by searching NuGet package metadata and filtering package IDs by naming convention:

- `Aspire.Hosting.*`
- `CommunityToolkit.Aspire.Hosting.*`

That makes NuGet package identity the user-facing integration identity. It also limits discovery to NuGet, prevents richer metadata, and makes the official/community distinction implicit in package naming.

This proposal introduces a curated integration-index layer above package providers. An integration index maps stable user-facing integration names to one or more provider coordinates such as NuGet packages, npm packages, container images, templates, or future provider types.

The first implementation should keep package installation behavior NuGet-based and reuse the existing `PackagingService`, `PackageChannel`, and `INuGetPackageCache` machinery. The index changes discovery and naming, not the package-resolution engine.

## MVP cut

The smallest useful implementation should be intentionally conservative:

1. Add built-in `aspire` and `community-toolkit` index artifacts.
2. Support only the NuGet provider.
3. Keep the existing NuGet version-resolution, feed, hive, staging, PR, and `ASPIRE_CLI_PACKAGES` behavior.
4. Preserve every existing package-derived friendly name as an alias.
5. Add index/provenance metadata to human-readable output without changing install semantics.
6. Move CLI, MCP, and integration-doc lookup to one shared integration-resolution service.
7. Keep user-added external indexes out of the first implementation except for the internal abstractions needed to avoid painting the design into a corner.

This cut is enough to validate the hard parts:

- Whether curated names can replace package IDs without breaking existing add/search flows.
- Whether "channel" can disappear from public integration UX by treating stable/daily/staging/PR/local as `aspire@variant`.
- Whether runtime availability and NuGet provider resolution can stay fast when the catalog is data-driven.
- Whether MCP, VS Code, and JSON consumers can migrate without relying on NuGet prefix-derived names.
- Whether the index artifact format can support future external indexes without requiring executable plugins.

It deliberately does not prove npm/container/template providers or arbitrary third-party index acquisition. Those should wait until the built-in NuGet-only path is stable.

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

Indexes should be authored as normal repo files. The authoring format should be JSON so entry files, generated artifacts, schemas, and CLI consumption all use one format. The CLI should consume a generated, validated JSON artifact.

Recommended repo shape for built-in indexes:

```text
src/Aspire.Cli/IntegrationIndexes/
  schema/
    integration-index.schema.json
  aspire/
    redis.json
    postgres.json
  community-toolkit/
    orleans-redis.json
  generated/
    aspire.index.json
    community-toolkit.index.json
```

The authoring files are optimized for review. The generated JSON files are optimized for the CLI and for remote acquisition:

- Contributors edit one small entry file per integration.
- CI validates entry files against the schema.
- CI regenerates deterministic `*.index.json` artifacts.
- The CLI embeds the generated artifacts at build/package time.
- Remote GitHub-backed indexes expose the generated artifact at a commit SHA.

Committing generated artifacts is a trade-off. It creates larger PR diffs and potential merge conflicts, but it also gives the CLI a simple immutable artifact to fetch, digest, cache, and embed without cloning or running repo code. If that proves too noisy, the fallback is to generate during build for built-in indexes and require external indexes to publish a generated artifact.

Example entry:

```json
{
  "id": "redis",
  "displayName": "Redis",
  "description": "Adds Redis hosting support.",
  "aliases": ["cache"],
  "tags": ["cache", "database"],
  "provenance": "official",
  "providers": [
    {
      "type": "nuget",
      "package": "Aspire.Hosting.Redis",
      "languages": ["csharp", "typescript", "python"],
      "resolution": {
        "prerelease": "variant"
      }
    }
  ]
}
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
          "resolution": {
            "prerelease": "variant"
          }
        }
      ],
      "docs": null
    }
  ]
}
```

`minCliVersion` is optional. When present and newer than the running CLI, the entry is treated as unavailable with a clear reason rather than being installed through partially understood metadata.

### Name conflicts

Entry IDs are unique only within an index, so `aspire/redis`, `community-toolkit/redis`, and `contoso/redis` can coexist. The generated artifact validator should reject duplicate entry IDs within one index and should reject aliases that conflict with another entry ID or alias in the same index unless they point to the same entry.

Cross-index conflicts are handled at resolution time. The index-qualified ID is the stable identity, and bare names are shortcuts that use the precedence rules in [Index and name precedence](#index-and-name-precedence). Human-readable list/search output should always include index and provenance when multiple enabled indexes expose the same bare name so users can disambiguate with commands such as `aspire add community-toolkit/redis`.

## Index acquisition

Indexes should not require a clone in the normal path.

Normal list/search/add commands should not fetch or switch index artifacts based on age. They should use the latest valid artifact already available locally:

1. Use a project-pinned artifact when the project pins an external index.
2. Otherwise use the newest valid cached generated artifact for the index.
3. For built-in indexes, compare the cache with the embedded snapshot when the CLI can order their revisions, then use the newest valid local artifact. If revisions cannot be ordered, prefer the project pin or cached artifact over the embedded snapshot to avoid silent downgrades.
4. For built-in indexes with no valid cache, use the embedded snapshot.
5. For external indexes with no valid cache or project pin, fail with instructions to run `aspire integration index update` or re-add the index.

Fetching happens through explicit index-management operations such as `aspire integration index add` and `aspire integration index update`:

1. Fetch a generated artifact or archive from the index endpoint.
2. Validate the artifact schema and index metadata.
3. Cache the artifact with the resolved commit or immutable version.

For GitHub-backed indexes, the preferred artifact is a generated JSON file committed in the index repository and fetched by commit SHA. A release asset or branch-generated artifact is acceptable only when the CLI can bind it to immutable content with a digest and index metadata. `aspire integration index update` advances a cached index from one resolved commit/digest to another; project-pinned indexes do not advance silently.

The embedded snapshot is a local artifact, not a freshness boundary. Normal commands should not perform opportunistic refreshes and should not switch artifacts based on cache age. Users can run `aspire integration index update` to advance cached indexes intentionally, similar to package managers that separate update from install/search. CLI releases should refresh the embedded snapshot so first-run/offline behavior does not drift indefinitely from the official index.

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
aspire integration index add contoso https://github.com/contoso/aspire-index --ref v1.2.3
aspire integration index update
aspire integration index remove contoso
```

Project-level index pins should be recorded when a project uses a non-built-in index. This avoids silently changing integration meaning when an external index moves.

Git-backed index references can be branches, tags, or commit SHAs at configuration time. The cache and project pin should store the resolved commit SHA and artifact digest, not the original mutable ref, so later commands can detect drift and reproduce the same index content.

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
8. Apply variant quality and provider compatibility filters.
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

The implicit/default Aspire variant must always participate in NuGet provider resolution, even when an AppHost has pinned an explicit variant. This preserves the current invariant from `IntegrationPackageSearchService.GetIntegrationPackagesWithChannelsAsync`: prerelease-only integrations must remain reachable through the implicit `Quality.Both` channel even when a polyglot AppHost pins a stable-quality channel. Entry compatibility and resolution hints filter candidates after variants have produced them; they must not narrow the variant set. This avoids regressing [#17724](https://github.com/microsoft/aspire/issues/17724) and [#17725](https://github.com/microsoft/aspire/issues/17725).

## Walking-skeleton implementation

The implementation should start by introducing the index model behind the existing command behavior, not by adding new UX first.

Proposed internal types:

| Type | Responsibility |
| ---- | -------------- |
| `IntegrationIndexDocument` | Deserialized generated artifact: schema version, index metadata, entries. |
| `IntegrationEntry` | Curated entry ID, display name, aliases, tags, docs, provenance, provider references. |
| `IntegrationProviderReference` | Provider type plus provider-specific coordinate, compatibility constraints, and resolution hints. |
| `IntegrationIndexVariant` | Publicly modeled index variant such as `aspire@daily`; internally maps to one or more `PackageChannel` instances for NuGet. |
| `IntegrationCandidate` | Runtime candidate after AppHost language, index precedence, alias, availability, and provider filtering. |
| `IIntegrationIndexSource` | Loads built-in, cached, external, or dynamic-discovery indexes. |
| `IIntegrationProvider` | Resolves provider versions and installs provider coordinates. First implementation only needs `NuGetIntegrationProvider`. |
| `IntegrationResolver` | Shared CLI/MCP/VS Code-facing service for search, exact resolution, ambiguity handling, and provider selection. |

The first code slice should preserve old behavior by construction:

1. Load built-in index documents from embedded JSON.
2. Build a legacy alias map from existing friendly-name algorithms.
3. Ask `PackagingService.GetChannelsAsync()` for the current NuGet resolution profiles.
4. Convert the selected profiles into `IntegrationIndexVariant` values.
5. Resolve integration entries to NuGet provider coordinates.
6. Use existing `PackageChannel` version APIs for availability and version selection.
7. Install through existing `IAppHostProject.AddPackageAsync`.

This lets the first PR be validated with current tests plus focused new tests:

- `aspire add redis` resolves to the same NuGet package/version it does today.
- `aspire add Aspire.Hosting.Redis` still works as an exact package ID fallback.
- `aspire add communitytoolkit-*` still works through generated aliases.
- Non-interactive fuzzy matches still fail rather than picking an arbitrary candidate.
- MCP listing and CLI listing return the same integration set.
- PR/local/`ASPIRE_CLI_PACKAGES` hives still produce the selected local version.

### Adapter strategy

The safest path is to adapt existing services incrementally:

```text
Current command code
  -> IntegrationPackageSearchService
      -> IntegrationResolver
          -> IIntegrationIndexSource[]
          -> IIntegrationProvider[]
          -> PackagingService / PackageChannel
```

`IntegrationPackageSearchService` can initially keep its public methods and return shapes while delegating to `IntegrationResolver`. That avoids rewriting `AddCommand`, `IntegrationSearchCommand`, and `ListIntegrationsTool` in one step. Once the resolver is stable, command-specific types can be renamed or collapsed around the integration model.

### Provider/version performance

A naive index implementation would query NuGet versions for every catalog entry on every list/search. That is too expensive and would make curated indexes slower than today's prefix search.

The NuGet provider should use a two-level strategy:

1. For list/search, reuse channel-level integration package search results when available and join those packages to index provider coordinates.
2. For exact add/index-qualified add, resolve the selected provider coordinate directly with `GetPackageVersionsAsync`.

That keeps normal discovery close to today's cost while still allowing exact curated entries that were not returned by a broad package search to produce a precise "entry exists but package is unavailable" error.

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

## Version and revision handling

The index should not introduce a second package-version axis. It is a catalog that maps a stable integration ID to provider coordinates. It can constrain or hint provider resolution, but package versions remain owned by the provider ecosystem.

Separate the concepts:

| Concept | Example | Owner | Meaning |
| ------- | ------- | ----- | ------- |
| Index schema version | `1` | Integration index format | Format compatibility between the CLI and generated index artifact. |
| Index artifact revision | Git commit + digest | Index source | The exact catalog content the CLI embedded, cached, or pinned. This is not an installable integration version. |
| Integration identity | `aspire/redis` | Index and entry ID | Stable name for search/add and provenance. It has no independent semantic version in the MVP. |
| Provider artifact version | `13.4.0` for `Aspire.Hosting.Redis` | NuGet, npm, container registry, or other provider | The installable artifact version selected by provider resolution. |
| Aspire compatibility | `[13.0.0,14.0.0)` | Index hints plus provider validation | Constraint used to filter provider artifact versions for a project. |
| Installed version | Package reference or Aspire config value | Project | The provider artifact version recorded by `aspire add`. |

The `--version` option applies to the selected provider artifact, not to the index entry:

```bash
aspire add redis --version 13.4.0
```

This means:

- `aspire/redis` names the catalog entry.
- `aspire@daily` selects the Aspire catalog variant and provider resolution profile.
- `nuget:Aspire.Hosting.Redis` is the provider coordinate.
- `13.4.0` is the NuGet package version.
- The index commit/digest identifies which catalog metadata was used.

Example provider resolution hints:

```json
{
  "providers": [
    {
      "type": "nuget",
      "package": "Aspire.Hosting.Redis",
      "resolution": {
        "aspireCompatibility": "[13.0.0,14.0.0)",
        "prerelease": "variant"
      }
    }
  ]
}
```

Default NuGet provider version behavior:

1. Query package versions from selected index variants.
2. Filter by variant quality.
3. Filter by entry compatibility hints when present.
4. Prefer the installed CLI/SDK version for local/PR hives when available.
5. Prefer the configured variant for polyglot AppHosts when present.
6. Otherwise select the latest compatible version using semantic version precedence.

The `aspireCompatibility` range is evaluated against the AppHost's resolved Aspire SDK/hosting version. C# AppHosts should use the resolved `Aspire.Hosting`/SDK version from the project. Polyglot AppHosts should use the SDK version stored in Aspire configuration or resolved by the guest AppHost project model.

An explicit provider version should:

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

## Relationship to ATS projection and polyglot integrations

[#16110](https://github.com/microsoft/aspire/pull/16110) explores TypeScript-authored integrations that participate in the ATS projection pipeline. That work is complementary to the index model:

```text
Integration index:
  "Which integration package should this name resolve to?"

Provider restore/install:
  "How do I acquire that package for this AppHost?"

ATS projection:
  "What typed builder methods and capabilities does the restored integration expose?"
```

For C# integrations, the NuGet provider installs a package and C# APIs are available through normal assembly references. For polyglot integrations, a future provider such as `npm` can install or reference an integration host package, then ATS projection can generate the TypeScript/Python/etc. AppHost surface from that package's exported capabilities.

The index should not duplicate the ATS-generated API surface. It can contain coarse discovery metadata such as:

- Provider coordinate, such as `npm:@spike/aspire-deno`.
- Supported AppHost languages.
- Projection kind, such as `ats`, when the provider package requires code generation.
- Aspire compatibility constraints.
- Documentation and provenance.

The method-level surface, DTOs, annotations, callbacks, and resource capabilities stay owned by the provider package and ATS projection. In other words, `aspire add deno` can use an index entry to find `npm:@spike/aspire-deno`, but `addDenoApp`, `withDenoPermissions`, and deployment callbacks come from the restored package's ATS exports, not from the index.

This keeps the responsibilities separate:

- The index is a catalog and trust/provenance layer.
- The provider is the acquisition and version-resolution layer.
- ATS projection is the polyglot API-shape layer.

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
    "name": "Redis",
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

## Hard choices and recommended defaults

This section captures the choices that should get explicit review before implementation. The recommendations are intended to make the first implementation small enough to ship while preserving room for external indexes and non-NuGet providers.

### Choice 1: Is a channel an index, an index variant, or a provider option?

**Recommendation:** model channel as an index variant in public integration UX, backed by provider-resolution profiles internally.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| Keep `channel` as a peer concept | Minimal implementation churn. | Users must understand index, source, and channel. Ambiguity grows when external indexes define their own release lanes. |
| Make channel a provider option only | Accurate for NuGet feeds and quality. | Hard to explain `aspire@daily` style discovery because the catalog and provider resolution are selected separately. |
| Make channel an index variant | One public noun for discovery: `index`. Variants can own catalog plus provider resolution. | Requires compatibility aliases for existing channel terminology and careful docs/help updates. |

The first implementation should keep `PackageChannel` internally and expose `aspire@stable`, `aspire@daily`, `aspire@staging`, and synthesized PR/local variants in integration commands. Existing `--channel`-style configuration can remain as a compatibility layer until all callers move to variant terminology.

### Choice 2: Where do built-in index files live?

**Recommendation:** keep built-in authoring files in this repo under the CLI source tree and commit deterministic generated artifacts.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| This repo, under CLI source | Contributions look like normal Aspire PRs. CLI packaging can embed artifacts directly. | Adds generated files to source diffs unless generation happens only at build time. |
| Separate official index repo | Cleaner ownership boundary and independent index updates. | More repo/process overhead. Harder to guarantee CLI snapshot freshness and validate changes with CLI tests. |
| Docs repo or docs tree | Easy to review as content. | Blurs product docs and executable catalog artifacts. Harder to build/test/package. |

The first index should live with the CLI because the CLI is the first consumer and because the entry schema must evolve with CLI code. A separate repo can be introduced later if index updates need an independent cadence.

### Choice 3: Authoring format vs consumed artifact

**Recommendation:** author per-entry JSON files for review, consume generated canonical JSON.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| Author and consume one JSON file | Simple and no generation step. | Large merge conflicts, poor contribution ergonomics, hard to review one-entry changes. |
| Author per-entry JSON, consume generated JSON | Good PR diffs, one parser format, and stable runtime artifact. | Requires a generator/validator and generated artifact policy. |
| Consume repo tree directly | No generated artifact. | Requires clone/archive traversal and makes index integrity harder. |

Per-entry JSON keeps contribution diffs small without adding another parser format to build tooling or the CLI. The hard requirement is "small authoring files plus deterministic generated JSON".

### Choice 4: What does project pinning look like?

**Recommendation:** do not persist built-in index pins in Phase 1; require project pins before external indexes are enabled.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| No project pins | Simple and matches today's package-only model. | External index meaning can drift after install. |
| Pin every index use | Fully reproducible. | Adds files/metadata for normal built-in Aspire scenarios and creates migration burden. |
| Pin only non-built-in indexes | Keeps built-in path simple while preventing silent external index drift. | A project using only built-in indexes is still tied to the installed CLI/package feed behavior. |

The storage location remains the hardest unresolved detail. A cross-AppHost lock file is attractive because it works for C# and polyglot AppHosts:

```text
<AppHost directory>/.aspire/integration-indexes.lock.json
```

Example:

```json
{
  "version": 1,
  "indexes": [
    {
      "id": "contoso",
      "url": "https://github.com/contoso/aspire-index",
      "commit": "abc123",
      "sha256": "..."
    }
  ]
}
```

The downside is introducing a new committed project file. The alternative is to store C# pins in MSBuild and polyglot pins in Aspire config, but that splits the implementation and makes cross-language tooling harder.

### Choice 5: How strict is index integrity?

**Recommendation:** bind every remote index artifact to index ID, resolved commit, schema version, and SHA-256 digest; do not require signed indexes in the first implementation.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| URL-only cache | Easy. | Allows silent content drift and cache poisoning by mutable refs. |
| Commit plus digest | Strong immutable artifact identity and straightforward project pins. | Requires a fetch path that can resolve refs to commits and compute digests. |
| Signed index artifacts | Stronger provenance story. | Requires key management, rotation, enterprise policy, and contributor education before there is evidence we need it. |

The digest is security-related here because it protects the index artifact the CLI consumed. It does not replace NuGet signing, npm provenance, or registry policy for provider artifacts.

### Choice 6: Should list/search resolve provider versions eagerly?

**Recommendation:** list/search should join index entries to existing provider search results; exact add should resolve provider versions directly.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| Eagerly query every indexed package | Strong "listed means installable" guarantee. | Slow and expensive, especially with multiple indexes/variants. |
| Never check availability in list/search | Fast and stable. | Search results can fail at install time more often than today. |
| Join broad provider search results, direct-resolve exact adds | Keeps normal cost near today's NuGet search and gives precise errors for exact requests. | Some curated entries can be hidden from normal search if broad provider search misses them. |

The fallback for hidden-but-valid entries is exact or index-qualified add: `aspire add aspire/foo` can resolve `foo` directly and explain why it is unavailable if no compatible package exists.

### Choice 7: How should JSON output evolve?

**Recommendation:** preserve existing `name`, `package`, and `version` fields initially; append `qualifiedName`, `index`, `provenance`, and `provider`.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| Replace shape immediately | Clean model. | Breaks tools that parse current JSON. |
| Append fields in-place | Most consumers tolerate additive fields. | `name` semantics remain ambiguous during migration. |
| Add a versioned output mode | Clear contract. | More CLI surface and test matrix. |

The key migration rule is that `name` must remain a valid add argument during the transition. Curated entry IDs can appear in `qualifiedName` before they replace legacy friendly names as the primary `name` value.

### Choice 8: Should third-party NuGet tag discovery be on by default?

**Recommendation:** curated indexes are default; NuGet tag discovery is explicit or policy-controlled.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| On by default | Maximum discoverability. | Search results become less curated and trust/provenance becomes more important. |
| Ask on first use | User-aware. | Awkward for non-interactive CLI, MCP, and VS Code flows. |
| Explicit opt-in | Predictable and safer for enterprise users. | Users may miss useful third-party integrations. |

The curated index should answer "what do we recommend?" Dynamic NuGet tag discovery should answer "what else exists?" and should be surfaced with `third-party` provenance.

### Choice 9: Do we need explicit provider coordinates?

**Recommendation:** support explicit provider coordinates such as `nuget:Aspire.Hosting.Redis` as the escape hatch.

| Option | Pros | Cons |
| ------ | ---- | ---- |
| Exact package ID fallback only | Preserves current usage. | Ambiguous once npm/container/template providers exist. |
| Explicit provider coordinates | Clear and extensible. | Adds another advanced syntax. |
| Require index entries for everything | Clean catalog model. | Blocks testing unpublished/internal packages and slows advanced workflows. |

Exact NuGet package IDs should keep working for compatibility, but the provider-coordinate form is the future-proof way to disambiguate package IDs from curated entry IDs.

### Choice 10: How much provider policy belongs in the index?

**Recommendation:** index entries declare policy intent and compatibility; providers enforce ecosystem-specific rules.

Examples of index-owned policy:

- Entry ID, aliases, display text, docs.
- Supported AppHost languages.
- Provider preference order.
- Aspire compatibility ranges.
- Whether prerelease follows variant behavior.
- Deprecation/replacement metadata.

Examples of provider-owned policy:

- NuGet source mapping and feed trust.
- NuGet version enumeration and package metadata validation.
- npm provenance and package-manager behavior.
- Container registry auth, digest pinning, and platform support.

Putting provider-specific policy entirely in the index would make indexes too powerful and too hard to trust. Putting all policy in providers would make indexes little more than search aliases. The split above keeps indexes data-only while still making curation meaningful.

## Incremental delivery strategy

The riskiest implementation would replace discovery, naming, version resolution, command output, MCP behavior, and external index acquisition in one change. This should be treated as a compatibility migration, not a rewrite.

Delivery rules:

1. Change one axis at a time: model, data, resolver, output, external acquisition, then new providers.
2. Keep NuGet provider resolution on `PackagingService` and `PackageChannel` until index-based discovery has proven equivalence.
3. Preserve old package-derived names as accepted aliases before introducing curated names as the primary display value.
4. Make every phase independently shippable with a rollback path to the previous resolver.
5. Prefer additive output fields before changing existing fields.
6. Require equivalence tests for current list/search/add behavior before new UX is enabled.

The first implementation should run the new model in **shadow mode** before it becomes authoritative:

```text
legacy NuGet resolver -> current command behavior
index resolver        -> parallel candidate set used by tests/diagnostics
comparison            -> reports mismatches in package ID, version, alias, provider, and availability
```

Shadow mode should not silently fall back in production command paths. Its purpose is to make mismatches visible in tests and optional diagnostics before the command path changes. Once the candidate sets match for the existing official/community NuGet surface, the command adapter can switch to the index resolver while preserving the old public method shapes.

## Migration plan

Each phase should have an explicit decision gate. If a gate fails, the next phase should not expand the concept surface area.

### Phase 0: Baseline current behavior

- Add focused tests that capture today's `aspire add`, `aspire integration list`, `aspire integration search`, and MCP integration listing behavior.
- Include stable/daily/staging, PR/local hives, `ASPIRE_CLI_PACKAGES`, exact package ID fallback, friendly-name derivation, and non-interactive fuzzy failure behavior.
- Capture compatibility expectations for the current JSON shape and current human-readable output.

**Gate:** tests describe the current behavior closely enough that a resolver replacement cannot change names, packages, versions, availability, or non-interactive behavior without a deliberate test update.

### Phase 1: Data model and generated built-in indexes

- Add `IntegrationEntry`, `IntegrationProviderReference`, and `IntegrationIndex` data abstractions.
- Add JSON schema validation and deterministic generation for built-in Aspire and Community Toolkit index artifacts.
- Author initial index entries from the existing prefix-discovered package set.
- Preserve existing user-facing names by generating aliases from both current friendly-name schemes: `IntegrationPackageSearchService.GenerateFriendlyName` and `ListIntegrationsTool.GetFriendlyName`.
- Do not change command behavior.

**Gate:** generated built-in index artifacts cover the same current official/community package set, and index CI catches duplicate IDs, alias conflicts, invalid provider coordinates, and nondeterministic generation.

### Phase 2: Shadow resolver

- Add `IntegrationResolver`, `IIntegrationIndexSource`, and NuGet-only `IIntegrationProvider` abstractions.
- Load built-in indexes and convert existing `PackageChannel` instances into `IntegrationIndexVariant` values.
- Run resolver comparison tests against the legacy NuGet search pipeline.
- Keep command paths using the legacy resolver.

**Gate:** shadow resolver results match legacy resolver results for current supported scenarios, including package ID, selected version, aliases, availability, and AppHost language support.

### Phase 3: Compatibility adapter switch

- Support only NuGet providers.
- Keep existing NuGet index-variant/version/install behavior.
- Update `IntegrationPackageSearchService` to delegate to `IntegrationResolver` while keeping its existing public method shapes where possible.
- Keep exact package ID matching as a fallback.
- Keep current human-readable and JSON output fields stable.

**Gate:** existing `aspire add`, `aspire integration list`, `aspire integration search`, and MCP integration listing produce equivalent results for current official/community NuGet packages, with only additive index/provenance metadata.

### Phase 4: Additive CLI/MCP UX

- Show index/provenance in human-readable output.
- Add index-qualified names such as `aspire/redis`.
- Add JSON output fields such as `qualifiedName`, `index`, `provenance`, and `provider` without removing existing fields.
- Update MCP listing to use the shared integration service.
- Add ambiguity prompts for same name across indexes.

**Gate:** every old package-derived name remains an accepted alias, non-interactive ambiguity rules are tested, and JSON/MCP/VS Code consumers have a documented compatibility path.

### Phase 5: External indexes

- Add `aspire integration index list/add/remove/update`.
- Add generated artifact acquisition and cache.
- Add schema validation and index metadata.
- Record project index provenance for non-built-in indexes.

**Gate:** remote index acquisition validates ID, schema, commit, and digest; project pins persist URL, commit, and digest for every non-built-in index used by a project.

### Phase 6: Dynamic NuGet discovery

- Integrate #16882-style NuGet tag discovery as an optional `NuGetTagDiscoveryIndex`.
- Keep third-party discovery opt-in.
- Reuse dependency metadata validation for exact package ID flows.

**Gate:** curated search remains the default, third-party discovery is visibly marked, and enterprise/non-interactive policy can disable it.

### Phase 7: Additional providers

- Add new built-in providers only after the index/trust model is stable.
- Candidate provider types: `npm`, `container`, `template`, `module`.
- Each provider must define version resolution, install behavior, validation, trust policy, and any projection requirements.
- For polyglot integration packages, require an ATS projection contract so the index only selects the package and the restored package defines the generated AppHost surface.

**Gate:** provider-coordinate syntax, provider ordering, AppHost language selection, provider-specific trust policy, and ATS projection responsibilities are documented and tested with one concrete non-NuGet provider before adding more.

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

- Should generated index artifacts be committed for built-in indexes, or generated during build and only committed/published for external index repos?
- Should Community Toolkit entries be authored in this repo first, in the Community Toolkit repo first, or mirrored between both?
- Should `community-toolkit` be enabled by default, or listed as built-in but disabled until a user opts in?
- Is `<AppHost directory>/.aspire/integration-indexes.lock.json` acceptable as a committed project file for external index pins, or should C# and polyglot AppHosts use their existing project/config stores?
- Should `aspire add Aspire.Hosting.Redis` continue as an unqualified exact package ID fallback forever, or should help text steer advanced users to `nuget:Aspire.Hosting.Redis` once provider coordinates exist?
- Does additive JSON output preserve enough compatibility, or do VS Code/MCP consumers need a versioned output mode before index fields are added?
- Should index entries include a simple `projection` hint for ATS-backed provider packages, or should that be inferred entirely from provider package metadata after restore?
- What enterprise policy hooks are required before user-added indexes ship: allowed index URLs, allowed commits, provider allowlists, package-source allowlists, or all of these?
