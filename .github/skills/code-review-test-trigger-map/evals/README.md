# Evaluating the `code-review-test-trigger-map` skill

Two independent things are checked, and they must not be confused:

- **Structural evaluation (automated).** `tests/Infrastructure.Tests/TestTriggerMap/ReviewSkillTests.cs`
  validates that the skill is packaged the way GitHub Copilot code review expects (path, YAML
  frontmatter with `name`/`description`, activation triggers named in the description) and that
  [`cases.json`](./cases.json) is well formed and complete. **These tests say nothing about review
  quality** — a skill can be perfectly packaged and still review badly.
- **Semantic evaluation (manual loop, below).** Whether a Copilot review that loads this skill
  actually finds the seeded defects and stays quiet on the true-negative case.

## Corpus

[`cases.json`](./cases.json) holds the curated cases. Each case has:

| Field | Meaning |
|---|---|
| `id`, `title` | stable identifier and one-line description |
| `kind` | `positive` (defects must be found) or `true-negative` (nothing should be reported) |
| `category` | which part of the skill it exercises |
| `changed_files`, `diff_summary` | the scenario the reviewer sees |
| `expected_findings[]` | defects that must be reported, each with `evidence_required[]` |
| `expected_non_findings[]` | comments that count as false positives |

Cases `skip-glob-too-broad` and `lost-polyglot-route-weak-test` are modeled on the two real
defects from [PR #19939](https://github.com/microsoft/aspire/pull/19939).
`layer1-owned-project-input` is the true-negative guard against the most likely false positive
(asking for a manual map rule for something the ProjectGraph already owns).
`gated-job-run-output-wiring` covers workflow/`run_*` gate wiring, and `over-broad-all-route`
covers the cost side.

## Semantic evaluation loop

1. Build a scratch branch that reproduces a case's `diff_summary` over the listed
   `changed_files`, and open a draft PR from it.
2. Request a Copilot code review on that PR.
3. Confirm the skill was loaded: check the review's skill attribution in the review body or the
   agent session log. A case where the skill never activated scores as an activation miss, not
   as a recall miss — fix the `description` triggers first, then re-run.
4. Score the review against the case:
   - **recall** = reported `expected_findings` / total `expected_findings`;
   - **precision** = a comment counts as a false positive if it matches an
     `expected_non_findings` entry, or if it is unsupported by the evidence the skill requires;
   - **evidence** = each reported finding must satisfy every `evidence_required` item;
   - **duplication** = a comment already made by an existing review on the PR counts against
     precision.
5. Record the score per case, adjust `SKILL.md`, and re-run the full corpus. Changing the skill
   to fix one case must not regress another.

## Success criteria

| Metric | Target |
|---|---|
| Skill activation on the four `positive` cases | 4/4 |
| Recall of seeded high-confidence defects | ≥ 6/7 findings, and both PR #19939 findings always reported |
| False positives on `layer1-owned-project-input` | 0 |
| Findings satisfying all `evidence_required` items | 100% of reported findings |
| Duplicate comments (already covered by existing review feedback) | 0 |
| `Infrastructure.Tests.TestTriggerMap` suite | green |

Run the structural half with:

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --no-launch-profile -- \
  --filter-namespace "Infrastructure.Tests.TestTriggerMap" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```
