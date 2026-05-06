# Linus Agent History

## Learnings

### Comments Must Stand On Their Own Without Design-Doc References

**Rule:** All comments in code, tests, and scripts must be self-contained and understandable without referencing internal design specifications.

**Forbidden patterns:**
- Design-doc filenames: `agreed-design-v3.md`
- Internal labels: `PR2-S<N>`, `PR2-spec`, `PR2 G<N>`
- Design-doc section references: `§N.N`, `§G<N>`, `Spec §N.N`
- Design terminology specific to internal specs: `Mode A`, `Mode B`, `sidecar primitive` (use actual locations instead: "parent-directory sidecar", "sibling sidecar")
- Spec references: `Acquisition v3`, `the v3 spec`, `PR2 design contract`

**Replacement approach:** Restate the actual invariant or behavior in plain English as if writing for a future maintainer with no access to design docs.

**Examples:**
- ❌ `// Per agreed-design-v3.md §2.4, return the resolved binary's directory...`
- ✅ `// Return the resolved binary's directory as the prefix so downstream consumers can still locate the binary even when the sidecar is absent.`

- ❌ `// Spec §2.4: when neither the Mode B sibling nor Mode A parent sidecar is present...`
- ✅ `// When neither the sibling sidecar (next to the binary) nor the parent-directory sidecar (one level up) is present...`

**Where this applies:** Production code, test code, inline comments, XML doc comments, and installer scripts.

**How to identify:** Use pattern matching on git diff:
```bash
git diff origin/main..HEAD -- src/ tests/ eng/ | grep -nE '^\+.*\b(PR2-S[0-9]|PR2-spec|PR2 G[0-9]|Spec §|§[0-9]\.[0-9]|§G[0-9]|Acquisition v3|agreed-design-v3|per spec §|the v3 spec|PR2 design contract|sidecar primitive|Mode A sidecar|Mode B sidecar)'
```

**Date:** 2026-05-06
