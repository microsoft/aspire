# Aspire CLI API docs commands

## Overview

This specification describes a new `aspire docs api` command group for browsing, searching, and retrieving Aspire API reference documentation from `aspire.dev`.

The new command group lives under `aspire docs`, but it uses a different ingestion pipeline than the existing prose docs commands:

- `aspire docs` indexes `llms-small.txt`
- `aspire docs api` indexes `sitemap-0.xml` and uses direct API page routes as addressable items

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

- `https://aspire.dev/sitemap-0.xml` as the API catalog source
- A fixed markdown resolution rule that appends `.md` to the canonical API page URL
- ETag-based caching and disk persistence for both the sitemap and page content

The implementation should not hardcode the sitemap and markdown endpoints directly into the command handlers.

## Parsing strategy

The API pipeline consists of:

1. Fetch sitemap
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
