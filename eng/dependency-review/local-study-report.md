# Detection-only local paired study, 2026-10-05

**Result:** All four full-corpus outputs recognized all 19 prespecified targets
somewhere in their findings or evidence tables. This does not demonstrate causal
uplift: baseline already recognized them, the sample is small and synthetic, and
inherited guidance could contaminate baseline. The enhanced skill arm and repeat
are **guide-loaded only / invocation noncompliant**: their evaluator tool sets did
not expose `functions.skill`. No original PR/version defects were remediated.

## Frozen design and provenance

Before evaluator calls, `corpus.json` and `expected.json` were written separately,
scorer assertions passed, and eight exact prompts were persisted. The 15 cases
cover documented holds/coupled majors, minimum-host/types uncertainty, coupled
toolchains, feed 401, actual generated drift, harmless wrapper/CLI version
differences, policy 403, stateful image majors/platform gaps, selector/resolution
coherence without an advisory, legitimate patch/minor, encoded scope/traversal,
selection versus execution, stale head evidence, ordinary null/duplicate
invariant loss, and visual-only CSS. All excerpts are explicitly synthetic;
none is presented as a real run, upstream advisory or observed real defect.
H0/H1 are fixture head labels, not real commit SHAs.

All eight independent calls used `functions.task`, `agent_type=general-purpose`,
`mode=sync`, with `model`, `reasoning_effort`, and `context_tier` omitted. The
concrete model is unknown. This is matched input/requested-default configuration,
**not scientifically verified concrete-model control**. Initial four calls and
four independent subset repeats were each issued together through the parallel
tool wrapper. No evaluator was reused for a repeat.

Both review arms received original `code-review/SKILL.md` text from snapshot
`e5f8dc6b1194773458881b400b1e2c91f91a33d8`; only enhanced review added the dependency
instruction text. Both skill-assessment arms received original `pr-testing` text
from the same snapshot; only enhanced skill added the dependency evidence guide.
Baseline loading used read-only `git show`, not a claimed skill-tool invocation.
Neutral prompts prohibited repository searches, enhanced-file reads, expectations,
adjudications, checker output, prior outputs, networking and remediation.

The coordinator actually invoked `functions.skill({"skill":"pr-testing"})` during
setup. That is **not** an isolated evaluator invocation. Both enhanced skill
completion histories explicitly say the requested tool was unavailable. Output
resemblance to the guide is not invocation evidence. The parent accepted retaining
these outputs as guide-only behavior, separately from failed invocation compliance.
No replacement skill framework, hosted PR, push, or original-PR action occurred.

## Exact scores

TP/miss uses the frozen **finding-emission** rubric. Recognition also counts
explicit correct state/table fields; this secondary measure was added after
observing missing finding emission and is disclosed as posthoc. Two emission
misses in enhanced review/baseline skill were the host install blockage and
feed401 blockage, each correctly represented in execution rows. They are not
substantive false claims. A schema alias (`results` for `rows`) in the full
enhanced guide-only output was accepted without rewriting raw output.

| Arm | Finding TP / miss | Recognized / targets | FP / unsupported / duplicate | Supported non-target observations | Correct state / rows |
|---|---:|---:|---:|---:|---:|
| Baseline review | 19 / 0 | 19 / 19 | 0 / 0 / 0 | 2 | 13 / 15 |
| Enhanced review | 17 / 2 | 19 / 19 | 0 / 0 / 0 | 3 | 13 / 15 |
| Baseline skill snapshot | 17 / 2 | 19 / 19 | 0 / 0 / 0 | 0 | 15 / 15 |
| Enhanced evidence guide-only | 19 / 0 | 19 / 19 | 0 / 0 / 0 | 5 | 15 / 15 |
| Baseline review repeat | 3 / 1 | 4 / 4 | 0 / 0 / 0 | 1 | 2 / 3 |
| Enhanced review repeat | 4 / 0 | 4 / 4 | 0 / 0 / 0 | 1 | 3 / 3 |
| Baseline skill snapshot repeat | 4 / 0 | 4 / 4 | 0 / 0 / 0 | 0 | 2 / 3 |
| Enhanced evidence guide-only repeat | 4 / 0 | 4 / 4 | 0 / 0 / 0 | 0 | 3 / 3 |

| Arm | Relevant valid citations / cited occurrences | Correct head labels / rows | Correct explicit execution classifications / statements | Redis/template selection-execution labels |
|---|---:|---:|---:|---:|
| Baseline review | 83 / 83 | 15 / 15 | 29 / 29 | 2 / 2 |
| Enhanced review | 82 / 82 | 15 / 15 | 29 / 32 | 1 / 2 |
| Baseline skill snapshot | 76 / 76 | 15 / 15 | 29 / 29 | 2 / 2 |
| Enhanced evidence guide-only | 87 / 87 | 15 / 15 | 27 / 27 | 2 / 2 |
| Baseline review repeat | 12 / 12 | 3 / 3 | 2 / 2 | 2 / 2 |
| Enhanced review repeat | 15 / 15 | 3 / 3 | 6 / 6 | 2 / 2 |
| Baseline skill snapshot repeat | 16 / 16 | 3 / 3 | 4 / 4 | 2 / 2 |
| Enhanced evidence guide-only repeat | 19 / 19 | 3 / 3 | 6 / 6 | 2 / 2 |

Citations count repeated excerpt occurrences, not independent evidence sources.
Execution denominators include only explicit statements, so sparse outputs have
smaller denominators and must not be interpreted as better coverage. Head labels
are a mechanical fidelity check, not actual artifact verification. All four
full outputs separately rejected H0 evidence as validation of H1. Enhanced review
called pending, skipped, and queued consumers `blocked` rather than `unexercised`;
it did not claim they passed. Its narrative correctly distinguished Redis skip
and actual template execution even though one state label was wrong.

Independent repeats used feed401, selection/execution and ordinary invariant
loss. Baseline review changed from an explicit feed blockage finding to just
`blocked`, and emitted no feed/ordinary execution rows in the repeat. Baseline
review and baseline skill repeats called wrapper coverage a `defect`, whereas
their full outputs used different dominant labels. Enhanced review's repeat
corrected the Redis taxonomy to `unexercised`. Ordinary guard loss was consistently
detected; no arm invented a visual CSS test requirement. No causal interpretation
or statistical significance is warranted from these changes.

## Separate deterministic arm and validation

Only after blind outputs were written, the existing repository checker and exact
extension feed validator both exited 0 on this child worktree. This is not a
retest of seeded hosted controls or an original source PR.
`deterministic-fixtures.json` separately projects the holds and URL cases:
the existing checker emits five hold/coupled-major findings, approves encoded
scope URL A and rejects traversal URL B. The other corpus behaviors remain outside
that checker. Checker logs/results were never included in evaluator prompts.

The focused Python suite passed **25 tests** with
`python3 -m unittest discover -s eng/dependency-review -p 'test_*.py'`.
It covers the scorer's multi-target findings, misses,
false/unsupported claims, duplicate-noise counting, citation/head fidelity,
schema-only deviations, row-only recognition, omitted-case rejection, execution
denominators, and deterministic guard outcomes. No new package or .NET tooling
was required.

## Artifacts and reproduction

Repository-retained files: `corpus.json`, `expected.json`, `adjudication-plan.json`,
`deterministic-fixtures.json`, `study.py`, `test_study.py`, this report and
`local-study-results.json`. The results bundle preserves raw model output objects,
expanded annotations, exact scores, requested config, baseline snapshot and
invocation provenance; it does not replace the raw prompt/output files.

Exact prompts, raw outputs, snapshot texts, invocation records, expanded
annotations, scores and deterministic logs are retained at:

```text
/workspaces/.copilotd/state/copilot-home/session-state/1479d4d4-b67c-4aef-be32-7c71a919841c/files/paired-study/
  baseline-{code-review,pr-testing}.txt
  {baseline,enhanced}-{review,skill}{,-repeat}.prompt.txt
  {baseline,enhanced}-{review,skill}{,-repeat}.json
  {baseline,enhanced}-{review,skill}{,-repeat}.adjudication.json
  {baseline,enhanced}-{review,skill}{,-repeat}.scores.json
  configuration.json
  invocations.json
  deterministic-{check,feed}.txt
  deterministic-corpus.json
```

The exact task envelope is recorded in `configuration.json`; each uses the
corresponding absolute prompt path. Completion histories and agent IDs are
recorded in `invocations.json`. Detailed successful evaluator tool event traces
are not exposed by `read_agent`; completion notes are transcriptions, not an
independent audit log. Skill invocation compliance is 0/2 enhanced evaluator
calls, irrespective of behavioral scores.

## Limits and candidate guidance refinements

Inherited system, AGENTS and repository rules (including dependency guidance,
approved feeds, action pins, ordinary correctness and visual-only test rules)
cannot be removed from task agents. Baseline therefore is not a clean-room
untreated control. Cases directly supply constraints, prerequisites and evidence
gaps, making detection easier than discovering them in a real PR. No actual
minimum-host, compile/lint, image migration, privileged workflow or advisory
query was performed for fixtures. Adjudication was coordinator-authored and
unblinded; relevance and supported non-target distinctions are subjective.
Requested default settings do not establish the concrete model or sampling
parameters. Hosted controls are parent-owned and not scored here.

Candidate changes for parent decision, **not applied to guidance**:

- Define pending/queued/skipped as `unexercised`; reserve `blocked` for a failed
  prerequisite or denied access, including dependent consumers when explained.
- Put a prerequisite gap once in the consumer table; do not force duplicate
  findings merely to satisfy an output schema.
- Keep passing patch/minor scenarios explicitly exercised with a bounded
  advisory-lookup caveat, rather than changing the whole row to unverified or
  emitting routine caveats as review defects. Enhanced review's patch row and
  five extra guide-only observations motivate this noise distinction.

No result certifies compatibility or upstream safety, and no automatic metric
is a compatibility certificate.
