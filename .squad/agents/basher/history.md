# Basher — Packaging / Infra — Session History

## 2026-05-06: C-001 Regex Coverage Gap Lesson

**Task**: Targeted scrub of 5 C-001 violations in `eng/` domain flagged by Saul (Pattern Steward).

**Violations Resolved**:
1. `eng/clipack/Common.projitems:55` — Removed "(acquisition design v3 §2.2 / §3.4)" from sidecar route comment
2. `eng/scripts/get-aspire-cli-pr.ps1:1329` — Deleted signpost "See acquisition design v3 §2.2"
3. `eng/scripts/get-aspire-cli-pr.sh:1110` — Deleted signpost "See acquisition design v3 §2.2" (cross-platform parity with ps1)
4. `eng/scripts/verify-cli-tool-nupkg.ps1:195` — Deleted signpost "See acquisition design v3 §2.3"
5. `eng/winget/microsoft.aspire/Aspire.installer.yaml.template:18` — Replaced "(acquisition design v3 §3.4)" with descriptive intent "system directories like %PROGRAMFILES%"

**Commit**: `0f7721260e` — "cleanup: remove design-doc references from eng/ comments (Saul C-001 sweep)"

### Lesson: Regex Coverage Must Include Phrasing Variants

Earlier C-001 sweep regex failed to catch these 5 violations because they used the **prose form** ("acquisition design v3") rather than the **filename form** ("agreed-design-v3.md").

**Pattern that was missed**:
- Filename: `agreed-design-v3.md`
- Prose references: `acquisition design v3` (with spaces in middle, sometimes with §references)
- Earlier regex only matched one form, not both

**Recommendation for future sweeps**:
- When creating spec-reference detection patterns, list both forms explicitly:
  - Filename pattern: `agreed-design-v3`
  - Prose pattern: `acquisition design v3` (with and without §refs)
  - Section refs: `§[0-9]+\.[0-9]+` (catches both forms)
- Test regex against both canonical forms before deployment
- Regex must account for phrasing as it appears in natural language comments, not just the technical identifier

**Verification**: Final regex sweep over PR2 diff shows zero hits for the broader C-001 pattern, confirming complete remediation.
