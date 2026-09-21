# Telemetry hook maintenance

The two embedded `track-telemetry` scripts are synchronized directly from
`microsoft/aspire-skills`, independently of skill distribution.
`telemetry-hooks.metadata.json` records their canonical version, repository,
immutable released-main commit, and SHA-512 hashes of LF-normalized UTF-8 without
a BOM. The existing `0.0.2` pin remains unchanged until a real source
synchronization; maintenance does not impose a minimum release version.

This maintenance path does not change `aspire agent init`, its runtime skills
installer, or its existing embedded skills archive and metadata. Hook installation
continues to use the compiled-in scripts without fetching them at runtime.

## Synchronizing and verifying

The `Update Telemetry Hooks` workflow requires `source_commit` and `version`
inputs through `workflow_dispatch`. It has no daily refresh schedule. The source
must be a full commit SHA on main's first-parent lineage, and its canonical plugin
version must match the requested version. A dev commit merged through a release
branch is not the released-main commit.

The updater fetches and validates both scripts before staging and publishing
either script or metadata. Replays are idempotent. Older or divergent sources and
version regressions cannot replace a newer synchronization. Serialized workflow
runs also check metadata on an existing `update-telemetry-hooks` PR branch, so
delayed dispatches cannot overwrite a newer unmerged update.

`Verify Telemetry Hooks` requires complete standalone metadata and matches the
local scripts' normalized hashes to both metadata and canonical source at the
pinned commit. Missing data, malformed provenance, failed fetches, and mismatches
fail verification. This hook-only path does not use a skill archive, archive
attestation, legacy arguments, or archive fallback.

From the repository root:

```powershell
.\eng\scripts\update-telemetry-hooks.ps1 -SourceCommit "<released-main-sha>" -Version "<release-version>"
.\eng\scripts\verify-telemetry-hooks.ps1
```

Direct invocations can also supply `-BaselineMetadataPath` with pending-PR
metadata; the tracked pin is always checked as well.

The workflow creates a draft PR on `update-telemetry-hooks`, matching the protected
script branch guard. The branch-name guard is not proof of bot identity;
canonical source verification remains independent. This is downstream receiver
work for [#20246](https://github.com/microsoft/aspire/issues/20246) and epic
[#20245](https://github.com/microsoft/aspire/issues/20245). The upstream sender and
dispatch credentials are separate work; deploy the receiver before enabling
upstream notifications.
