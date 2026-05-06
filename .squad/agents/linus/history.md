# Linus Agent History

## Learnings

### Comments Must Stand Alone (Part 1): Design-Doc References Are Public Liability

**Date:** 2026-05-06

**Rule:** Code and test comments MUST stand on their own without referencing internal design documents, design-spec sections (§), internal goal-group labels, or internal task labels.

**Why:** Comments and assertion messages are part of the final repository artifact visible to all future maintainers and external contributors. Once committed, they become searchable history. References to internal design docs create confusion, break links if docs are reorganized, and presume a shared understanding that future readers won't have.

**Forbidden Patterns:**
- `PR1-S<N>` / `PR1-spec` / `PR1 G<N>` (design-phase labels)
- `Spec §<section>` or `§<section>` (section numbers in design docs)
- `Acquisition v3`, `the v3 spec`, `PR1 design contract` (spec-era terminology)
- Goal-group prose: `cross-route channel contamination`, `route-aware update`, `sidecar primitive`
- Filenames: `agreed-design-v3.md`

**Example transformation:**
```csharp
// ❌ "PR1-S7 removed the global-channel read fallback."
// ✅ "Channel resolution uses per-project aspire.config.json only, never the global config."
```

---

### Comments Must Stand Alone (Part 2): No Removal/Negation Framing

**Date:** 2026-05-06 (strengthened rule)

**Rule:** Comments must describe what the code DOES, not what was removed, deferred, or "no longer" present. The diff is against `origin/main` — from that perspective, removed code was never there. Explaining its absence is meaningless to a fresh reader.

**Why:** When reviewing code without knowledge of the design doc or prior state, a comment like "we removed X" creates confusion. The reader doesn't see what was removed, so the comment doesn't clarify the current behavior — it only documents ancient history.

**ALSO FORBIDDEN (in addition to Part 1):**
- `"no longer reads ..."`, `"no longer consults ..."`
- `"was removed"`, `"was deleted"`, `"fell back to"`
- `"we removed"`, `"we don't do"`, `"we chose not to"`
- Comments that only make sense if reader knows what we deleted
- XML doc text like `"removed in PR1-S10 ..."` or `"now-removed global-channel fallback"`

**Replacement rule:** Either **DELETE the comment entirely** (the absence speaks for itself), OR **rewrite as a POSITIVE statement of CURRENT behavior** (what the code DOES now).

**Examples:**

```csharp
// ❌ "PR1-S7 removed the global-channel read fallback."
// ✅ "Channel resolution uses per-project aspire.config.json only."

// ❌ "The global-channel read fallback was removed..."
// ✅ "Channel resolution queries per-project aspire.config.json only, never the global ~/.aspire/aspire.config.json."

// ❌ "We removed the IConfigurationService dependency. It was deleted here."
// ✅ (Just delete the comment—the missing dependency speaks for itself. TemplateNuGetConfigService.Ctor
//     will not accept IConfigurationService; the constraint is enforced structurally.)

// ❌ Comment block explaining deleted test: "Pre-existing test X was deleted: it exercised the now-removed
//     global-channel fallback (FakeConfigurationServiceWithChannel → TemplateNuGetConfigService) that PR1 G1
//     forbids. With ResolveTemplatePackageAsync no longer reading the global config, the only way init can
//     pick up a non-implicit channel is via an explicit query parameter..."
// ✅ "Channel resolution uses explicit input or per-project aspire.config.json only; coverage in TemplateNuGetConfigServiceTests."

// ❌ XML doc: "Spec-derived regression tests for PR1-S10: project-channel reseed sites read the value to persist
//     from CliExecutionContext.Channel (option-(a) resolved label — pr-<N> for PR builds..."
// ✅ "Regression tests for project-channel reseed sites, ensuring that the resolved channel label from
//     CliExecutionContext.Channel (pr-<N> for PR builds, identity verbatim otherwise) is correctly persisted."
```

**Scope:**
- Apply to: `src/`, `tests/`, `eng/` (all production and test code, including YAML/script comments)
- Exempt: `.squad/`, `docs/specs/`, internal design docs (those ARE where labels and removal history belong)
- Include: Test assertion messages (they appear in failure output that lands in CI logs)
- Exclude: Commit message bodies (those are committer notes, not in-code material)

**Verification (comprehensive pattern):**
```bash
git --no-pager diff origin/main..HEAD -- src/ tests/ eng/ | grep -nE '^\+.*\b(PR1-S[0-9]|PR1-spec|PR1 G[0-9]|Spec §|§[0-9]\.[0-9]|§G[0-9]|Acquisition v3|agreed-design-v3|per spec §|G[0-9] \(|cross-route channel contamination|route-aware update|the v3 spec|PR1 design contract|sidecar primitive|no longer reads|no longer consults|fallback was removed|we removed|chose not to)'
```
Should return **zero hits** after scrub is complete.


