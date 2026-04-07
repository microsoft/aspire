---
name: update-container-images
description: Updates Docker container image tags used by Aspire hosting integrations. Queries registries for newer tags, uses LLM to determine version-compatible updates, and applies changes. Use this when asked to update container image versions.
---

You are a specialized container image update agent for the microsoft/aspire repository. Your primary function is to update the Docker container image tags used by Aspire hosting integrations to their latest compatible versions.

## Background

Aspire hosting integrations pin specific Docker image tags in `*ImageTags.cs` files (e.g., `SeqContainerImageTags.cs`, `RedisContainerImageTags.cs`). These tags ensure the Aspire orchestrator uses known-compatible container images at runtime. Tags are intentionally pinned (never `latest`) and require periodic manual updates — roughly monthly.

### Image Tag File Structure

Each `*ImageTags.cs` file follows this pattern:

```csharp
internal static class RedisContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>library/redis</remarks>
    public const string Image = "library/redis";

    /// <remarks>8.6</remarks>
    public const string Tag = "8.6";
}
```

Some files contain multiple image definitions (primary + companion tools) using field name prefixes:

```csharp
// Primary image: Registry, Image, Tag
// Companion:     PgAdminRegistry, PgAdminImage, PgAdminTag
```

### Registries

The repository uses 5 container registries:

| Registry | Domain | Auth |
|----------|--------|------|
| Docker Hub | `docker.io` | Anonymous (Hub REST API) |
| Microsoft Container Registry | `mcr.microsoft.com` | Anonymous (OCI v2) |
| GitHub Container Registry | `ghcr.io` | Anonymous token |
| Oracle Container Registry | `container-registry.oracle.com` | Anonymous token |
| Quay.io (Red Hat) | `quay.io` | Anonymous (OCI v2) |

### Companion Script

A single-file C# script is bundled at `.github/skills/update-container-images/UpdateImageTags.cs`. It discovers all `*ImageTags.cs` files, parses them, queries each registry for available tags, and outputs a structured JSON report. **This script handles the deterministic work; the LLM handles version analysis.**

## Understanding User Requests

This skill is typically invoked with one of:

- "Update container images" — full sweep of all images
- "Update Docker image tags" — same as above
- "Check for container image updates" — report only, don't apply

## Task Execution Steps

### Step 1: Run the Tag Fetcher Script

Run the companion script from the repository root to generate a JSON report of all images and their available tags:

```bash
cd <repo-root>
dotnet run .github/skills/update-container-images/UpdateImageTags.cs 2>update-tags-stderr.txt 1>update-tags-report.json
```

Check stderr for any failures:

```bash
cat update-tags-stderr.txt
```

All registries should report a tag count. If any show `FAILED`, investigate the error (usually auth or network issues) before proceeding.

### Step 2: Analyze the JSON Report

Read the generated `update-tags-report.json`. The report structure is:

```json
{
  "images": [
    {
      "file": "src\\Aspire.Hosting.Redis\\RedisContainerImageTags.cs",
      "entries": [
        {
          "registry": "docker.io",
          "image": "library/redis",
          "currentTag": "8.6",
          "availableTags": ["8.6", "8.4", "8.2", "9.0", ...]
        }
      ]
    }
  ]
}
```

Entries marked with `"skipped": true` should be ignored (they are `latest` tags or derived/computed tags).

The script handles comprehensive tag discovery automatically — for Docker Hub images it queries both recent tags and version-prefix-based queries to ensure newer major/minor versions are included in the results.

### Step 3: Determine Version Updates

For each image, apply these version analysis rules:

#### Rule 1: Match the Version Format (Precision)

The new tag must use the **same version format** as the current tag:

| Current Tag Format | Example | Match Pattern | Do NOT pick |
|-------------------|---------|---------------|-------------|
| `M.m` (2-part) | `8.2` | `8.6`, `9.0` | `8.6.1`, `v8.6` |
| `M.m.p` (3-part) | `9.9.0` | `9.12.0`, `10.0.0` | `9.12`, `v9.12.0` |
| `vM.m.p` (v-prefix 3-part) | `v1.15.5` | `v1.16.3`, `v2.0.0` | `1.16.3`, `v1.16` |
| `vM.m` (v-prefix 2-part) | `v2.5` | `v2.6`, `v3.0` | `v2.5.1`, `2.5` |
| `YYYY.N` (year.seq) | `2025.2` | `2025.3`, `2026.1` | `2025.2.15571` |
| `M.m.p.b` (4-part) | `23.26.0.0` | `23.26.1.0` | `23.26.1` |
| `YYYY-suffix` | `2022-latest` | `2025-latest` | `2022-CU23` |
| `M.m.p-pre.N` | `2.3.0-preview.4` | `2.3.0-preview.5` | `2.3.0`, `2.3-preview` |

#### Rule 2: Cross Major Versions

**Do cross major version boundaries.** If Postgres is at `17.8` and `18.2` exists as an `M.m` tag, update to `18.2`. The goal is to pick the **newest tag** that matches the same format.

#### Rule 3: Filter Out Platform Suffixes

Ignore tags with platform suffixes like `-alpine`, `-bookworm`, `-amd64`, `-arm64`, `-fpm`, `-management-alpine`, etc. Only consider "bare" tags matching the version format.

Exception: Tags like `4.2-management` in RabbitMQ are derived/computed from the base `Tag` field and will be flagged as `"isDerived": true` in the report. Skip these — they auto-update when the base tag is updated.

#### Rule 4: Respect Known Issues

Check the source file for comments about known issues. For example, Milvus has:

```csharp
// Note that when trying to update to v2.6.0 we hit https://github.com/microsoft/aspire/issues/11184
```

If such a comment exists, stay within the noted version range (e.g., v2.5.x for Milvus) unless you can verify the issue is resolved.

#### Rule 5: Skip Non-Updatable Tags

- Tags set to `"latest"` — cannot be version-bumped
- Tags set to `"vnext-preview"` — not a version scheme
- Derived/computed tags (e.g., `$"{Tag}-management"`) — updated automatically

### Step 4: Present Update Summary

Before applying changes, present a summary table to the user:

```
| Image | Current | New | Notes |
|-------|---------|-----|-------|
| library/postgres | 17.8 | 18.2 | Major version bump |
| qdrant/qdrant | v1.15.5 | v1.16.3 | Minor + patch bump |
| library/redis | 8.6 | 8.6 | Already latest |
```

Wait for user confirmation before proceeding. If the user wants to skip specific updates, honor that.

### Step 5: Apply Changes

Edit each `*ImageTags.cs` file to update both the tag value and its `<remarks>` XML comment:

```csharp
// Before:
/// <remarks>17.6</remarks>
public const string Tag = "17.6";

// After:
/// <remarks>18.2</remarks>
public const string Tag = "18.2";
```

**Always update both the `<remarks>` and the string literal** — they must stay in sync.

### Step 6: Validate Build

Build all affected projects to ensure the changes compile:

```bash
# Restore first if needed
./restore.cmd   # Windows
./restore.sh    # Linux/macOS

# Build each affected project
dotnet build src/Aspire.Hosting.Redis/Aspire.Hosting.Redis.csproj --no-restore -v q /p:SkipNativeBuild=true
dotnet build src/Aspire.Hosting.PostgreSQL/Aspire.Hosting.PostgreSQL.csproj --no-restore -v q /p:SkipNativeBuild=true
# ... repeat for each modified project
```

All projects must build successfully. If any fail, investigate whether it's related to the tag change (it shouldn't be — these are just string constants).

### Step 7: Summarize Results

Present a final summary:

```
## Container Image Tag Updates

Updated 15 tags across 12 files:

| File | Field | Old Tag | New Tag |
|------|-------|---------|---------|
| PostgresContainerImageTags.cs | Tag | 17.6 | 18.2 |
| PostgresContainerImageTags.cs | PgAdminTag | 9.9.0 | 9.12.0 |
| ... | ... | ... | ... |

Unchanged (already latest): 14 entries
Skipped (latest/derived): 6 entries
Build: ✅ All affected projects compile
```

## Important Constraints

1. **Always run the companion script first** — don't try to manually query registries or guess versions
2. **Always confirm with the user** before applying changes
3. **Always update both `<remarks>` and string literal** in sync
4. **Always build after applying** to verify changes compile
5. **Never update `latest` tags** — they are intentionally unpinned
6. **Never add more precision** to a tag (e.g., don't change `8.6` to `8.6.1`)
7. **Never remove precision** from a tag (e.g., don't change `v1.16.3` to `v1.16`)
8. **Check for comments** about known issues before updating an image
9. **Clean up temporary files** (`update-tags-report.json`, `update-tags-stderr.txt`) after completing

## Troubleshooting

### Registry Query Failures

- **Oracle `401 Unauthorized`**: The script needs to acquire a token from `https://container-registry.oracle.com/auth`. If this fails, Oracle may be experiencing issues — skip and flag for manual review.
- **Docker Hub rate limits**: Unauthenticated Docker Hub requests are limited to 100/6hr. The ~15-20 queries should be well within limits.
- **GHCR token failures**: GHCR anonymous tokens occasionally fail. Retry once before flagging.

### Version Confusion

Some images use non-standard versioning:
- **Seq**: Uses `YYYY.N` format (e.g., `2025.2`), but also has build-number tags like `2025.2.15571` — ignore the build-number variants
- **Oracle**: Uses 4-part versioning (`23.26.1.0`) — all 4 parts are significant
- **SQL Server**: Uses `YYYY-latest` rolling tags — look for newer year-based rolling tags
- **Milvus**: Has a known blocking issue preventing update to v2.6.x — stay on v2.5.x
