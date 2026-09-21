# Telemetry hook maintenance

The two embedded `track-telemetry` scripts are synchronized directly from
`microsoft/aspire-skills`. Hook installation uses the compiled-in scripts without
fetching them at runtime.

`telemetry-hooks.metadata.json` records their canonical version, repository,
immutable released-main commit, and SHA-512 hashes of LF-normalized UTF-8 without
a BOM.

## Synchronizing and verifying

The `Update Telemetry Hooks` workflow requires `source_commit` and `version`
inputs through `workflow_dispatch`. The source must be a full commit SHA on
main's first-parent lineage, and its canonical plugin
version must match the requested version. A dev commit merged through a release
branch is not the released-main commit.

The updater fetches and validates both scripts before staging and publishing
either script or metadata. Replays are idempotent. Older or divergent sources and
version regressions cannot replace a newer synchronization. Serialized workflow
runs also check metadata on an existing `update-telemetry-hooks` PR branch, so
delayed dispatches cannot overwrite a newer unmerged update.

`Verify Telemetry Hooks` requires complete metadata and matches the
local scripts' normalized hashes to both metadata and canonical source at the
pinned commit. Missing data, malformed provenance, failed fetches, and mismatches
fail verification.

From the repository root:

```powershell
.\eng\scripts\update-telemetry-hooks.ps1 -SourceCommit "<released-main-sha>" -Version "<release-version>"
.\eng\scripts\verify-telemetry-hooks.ps1
```

Direct invocations can also supply `-BaselineMetadataPath` with pending-PR
metadata; the tracked pin is always checked as well.

The workflow creates a draft PR on `update-telemetry-hooks`, matching the protected
script branch guard. The branch-name guard is not proof of bot identity;
canonical source verification remains independent.
