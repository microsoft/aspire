# Aspire CLI API docs commands

## Overview

This specification describes a new `aspire docs api` command group for browsing, searching, and retrieving Aspire API reference documentation from `aspire.dev`.

The new command group lives under `aspire docs`, but it uses a different ingestion pipeline than the existing prose docs commands:

- `aspire docs` indexes `llms-full.txt`
- `aspire docs api` indexes the `sitemap-index.xml` sitemap index (or a plain `urlset` sitemap) and uses direct API page routes as addressable items

## Goals

1. Expose API reference content through the Aspire CLI.
2. Reuse the existing fetch, ETag, and disk-cache patterns where possible.
3. Preserve API hierarchy so the CLI can browse the catalog without dumping every API at once.
4. Support C# and TypeScript API reference pages.
5. Return markdown for `get` operations.

## Non-goals

- Full unscoped catalog listing.
- A general-purpose web crawler for all `aspire.dev` content.
- Cross-language identifier normalization beyond the Aspire API route structure.

## Command surface

```text
aspire docs api list <scope> [--format json]
aspire docs api search <query> [--language <language>] [--limit|-n <count>] [--format json]
aspire docs api get <id> [--format json]
```

## Browse model

`list` is a scoped browse command. It never returns the entire API catalog.

### Supported scopes

```text
aspire docs api list csharp
aspire docs api list csharp/<package>
aspire docs api list csharp/<package>/<type>

aspire docs api list typescript
aspire docs api list typescript/<module>
aspire docs api list typescript/<module>/<symbol>
```

### Scope semantics

| Scope | Returns |
| --- | --- |
| `csharp` | Top-level C# packages |
| `csharp/<package>` | Types in the package |
| `csharp/<package>/<type>` | Member-group pages for the type such as `methods`, `properties`, and `constructors` |
| `typescript` | Top-level TypeScript modules |
| `typescript/<module>` | Direct symbols in the module |
| `typescript/<module>/<symbol>` | Members under the symbol |

If a scope has no children, `list` returns no results instead of expanding sideways or returning sibling scopes.

## Identifier model

The CLI uses stable path-like identifiers derived from the API route structure.

### Base rule

For sitemap-backed pages, the identifier is the route path after `/reference/api/`, without a trailing slash.

Examples:

```text
csharp/aspire.azure.ai.inference
csharp/aspire.azure.ai.inference/aspireazureaiinferenceextensions
typescript/aspire.hosting.azure.appconfiguration
typescript/aspire.hosting.azure.appconfiguration/azureappconfigurationresource
typescript/aspire.hosting.azure.appconfiguration/azureappconfigurationresource/runasemulator
```

## Hierarchy model

### C\#

The CLI exposes the following user-facing hierarchy:

```text
language -> package -> type -> member-group page
```

### TypeScript

The CLI exposes the following hierarchy:

```text
language -> module -> symbol -> member
```

This aligns with the sitemap more directly:

```text
module -> symbol -> member
```

## Search behavior

`search` performs weighted lexical matching over the indexed API map.

The index should prioritize:

1. Exact identifier matches
2. Exact API name matches
3. Package or module matches
4. Type or symbol matches
5. Member-group matches
6. Summary and description snippets when available from future index metadata

The `--language` option limits results to a supported API language. Today the supported language set is `csharp` and `typescript`.

Search results should include enough metadata for the user to copy the returned identifier directly into `aspire docs api get`.

## Get behavior

`get` resolves an exact API identifier and returns markdown content.

### Direct-route items

For items whose identifier maps directly to a page route, `get` fetches the markdown for that API page.

## Source and caching

The implementation uses:

- `https://aspire.dev/sitemap-index.xml` as the API catalog source, overridable with `docs:api:sitemapUrl`
- A fixed markdown resolution rule that appends `.md` to the canonical API page URL
- ETag-based caching and disk persistence for every sitemap and for page content

The implementation should not hardcode the sitemap and markdown endpoints directly into the command handlers.

### Sitemap index resolution

The configured source is resolved by its XML root element:

- `sitemapindex`: every `<sitemap><loc>` is fetched as a child sitemap and the child entries are merged. This lets a site list API routes outside `sitemap-0.xml`, for example in a `sitemap-api.xml` when API pages are rendered on demand instead of generated statically.
- `urlset`: used as is. This keeps overrides and deployments that list every API route in one sitemap working unchanged.

Child sitemaps are handled as follows:

- Each child uses its own ETag and content cache entry, so an unchanged child stays a `304 Not Modified` when another child changes.
- Child URLs are rebased onto the host of the configured sitemap URL, the same way page URLs are rebased, so a staging or local override reads only that host's sitemaps.
- Entries are unioned and de-duplicated, because a page can be listed in more than one child.
- The index fingerprint is computed from the merged entries in a deterministic order, so a change in any child rebuilds the index. The index schema version is bumped when the source shape changes so persisted indexes from older CLIs are rebuilt.
- If a child cannot be fetched and has no cached copy, the refresh fails and the CLI falls back to its cached index. If there is no cached index, a warning is logged and the catalog stays empty, rather than building a catalog that is missing part of the API surface.

Pages are always fetched from `<page-url>.md`. On-demand pages can respond with `Cache-Control: no-store` and no ETag; the page fetcher tolerates that and simply downloads the content again.

## Parsing strategy

The API pipeline consists of:

1. Fetch the sitemap index (or sitemap) and any child sitemaps
2. Parse C# and TypeScript API routes
3. Build a hierarchical API map
4. Persist the built index to disk
5. Use the index to serve `list`, `search`, and `get`

## Output models

### List

List output includes:

- `id`
- `name`
- `language`
- `kind`
- `parentId`

### Search

Search output includes:

- `id`
- `name`
- `language`
- `kind`
- `parentId`
- `score`

### Get

Get output includes:

- `id`
- `name`
- `language`
- `kind`
- `parentId`
- `url`
- `content`

## Testing

The feature requires tests for:

- sitemap parsing and filtering
- C# member-group page indexing
- TypeScript hierarchy parsing
- ETag-aware fetch behavior
- disk-persisted API index caching
- scoped `list`
- language-filtered `search`
- identifier-based `get`
